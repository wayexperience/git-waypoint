using Unity.Editor.Tasks;
using UnityEditor;

namespace Unity.VersionControl.Git.UI
{
    // Automatic background refreshes (the lock poll, the asset-change refresh) shouldn't flash the Git
    // window progress bar - it just looks like random activity. They mark a short quiet window here and
    // DoProgressGUI skips drawing the bar during it. User-initiated operations (commit/push/pull) don't
    // mark it, so their progress bars still show. We only gate the drawing, never the progress events
    // themselves, so the busy/redraw bookkeeping stays intact.
    static class BackgroundGitRefresh
    {
        public static double QuietProgressUntil;
        public static void Mark() { QuietProgressUntil = EditorApplication.timeSinceStartup + 6; }
        // A user-initiated operation calls this so its progress isn't hidden by a background quiet window.
        public static void Clear() { QuietProgressUntil = 0; }
        public static bool IsQuiet => EditorApplication.timeSinceStartup < QuietProgressUntil;
    }

    // The native repository watcher is unavailable on some platforms (e.g. Apple Silicon), so LFS
    // lock changes made outside this process - by teammates, or by an external "git lfs lock" - are
    // never noticed. Poll the lock list periodically so the UI stays roughly in sync.
    //
    // Crucially, never stack polls: "git lfs locks" talks to the server (over SSH/HTTPS), and if that hangs
    // - e.g. the SSH agent / 1Password is locked and can't authenticate non-interactively - firing a fresh
    // poll every 10s piles up stuck processes that jam the whole git task queue. So we only issue a new poll
    // once the previous one has come back, complain once if it doesn't, and back off until it recovers.
    [InitializeOnLoad]
    static class GitLocksPoller
    {
        private const double IntervalSeconds = 10;
        private const double StuckSeconds = 15;     // no result in this long => assume it's stuck
        private const double BackoffSeconds = 60;   // after a stuck poll, wait this long before retrying
        private static double nextTime;
        private static bool pending;
        private static double pendingSince;
        private static bool warned;
        private static IRepository hooked;

        static GitLocksPoller()
        {
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            try
            {
                var manager = EntryPoint.ApplicationManager;
                var repository = manager != null && manager.Environment != null ? manager.Environment.Repository : null;
                if (repository == null)
                    return;

                if (hooked != repository)
                {
                    if (hooked != null) hooked.LocksRefreshed -= OnLocksRefreshed;
                    repository.LocksRefreshed += OnLocksRefreshed;
                    hooked = repository;
                }

                if (pending)
                {
                    // A poll is in flight - don't issue another. If it never came back, it's stuck (likely a
                    // locked SSH agent): say so once and back off instead of hammering the queue.
                    if (EditorApplication.timeSinceStartup - pendingSince > StuckSeconds)
                    {
                        if (!warned)
                        {
                            UnityEngine.Debug.LogWarning("WAY Git: the LFS lock check isn't responding - is your SSH key / 1Password unlocked? Pausing lock polling for a bit.");
                            warned = true;
                        }
                        pending = false;
                        nextTime = EditorApplication.timeSinceStartup + BackoffSeconds;
                    }
                    return;
                }

                if (EditorApplication.timeSinceStartup < nextTime)
                    return;
                nextTime = EditorApplication.timeSinceStartup + IntervalSeconds;

                pending = true;
                pendingSince = EditorApplication.timeSinceStartup;
                BackgroundGitRefresh.Mark();
                repository.Refresh(CacheType.GitLocks);
                // Recompute outdated files here too (a cheap local diff), so the badges survive domain
                // reloads and stay current - the static set is otherwise wiped on every recompile.
                LfsLocksModificationProcessor.RefreshOutdated();
            }
            catch
            {
                // never let a poll break the editor loop
                pending = false;
            }
        }

        // A poll completed (fires on every refresh, even when the lock set didn't change): healthy again,
        // clear the pending/warning state.
        private static void OnLocksRefreshed()
        {
            pending = false;
            warned = false;
        }
    }

    // Fetch from the remote periodically so ahead/behind (and the "to pull" toolbar status) stay current
    // without a manual Fetch, keeping the worktree easy to keep up to date. Silent: failures (e.g. offline)
    // don't pop a dialog, and the progress bar is kept quiet. Interval comes from the plugin settings.
    [InitializeOnLoad]
    static class GitAutoFetch
    {
        private static double nextTime;
        private static double lastFetchStart;
        private static bool fetching;
        public static bool IsFetching => fetching;

        static GitAutoFetch()
        {
            EditorApplication.update += Tick;
        }

        // Let the UI ask for a prompt fetch (e.g. when the window opens or reloads) instead of waiting up to
        // the full interval, so you see incoming commits without pressing Fetch. Throttled so repeated
        // opens/focus changes don't hammer the server.
        public static void RequestFetchSoon()
        {
            if (fetching) return;
            if (EditorApplication.timeSinceStartup - lastFetchStart < 15) return;
            nextTime = 0;
        }

        private static void Tick()
        {
            if (fetching || !ApplicationConfiguration.AutoFetchEnabled || EditorApplication.timeSinceStartup < nextTime)
                return;

            try
            {
                var manager = EntryPoint.ApplicationManager;
                var repository = manager != null && manager.Environment != null ? manager.Environment.Repository : null;
                // Not ready yet (no repo/remote): leave nextTime alone so we fetch as soon as it is.
                if (repository == null || !repository.CurrentRemote.HasValue)
                    return;

                var interval = ApplicationConfiguration.AutoFetchInterval;
                if (interval < 1) interval = 1;
                nextTime = EditorApplication.timeSinceStartup + interval * 60;

                fetching = true;
                lastFetchStart = EditorApplication.timeSinceStartup;
                BackgroundGitRefresh.Mark();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews(); // surface "Fetching…"
                repository.Fetch()
                    .FinallyInUI((success, e) =>
                    {
                        fetching = false;
                        if (success)
                        {
                            LfsLocksModificationProcessor.RefreshOutdated(); // remote moved: recompute outdated files
                            // Fetch() already recomputes ahead/behind; also refresh the log and branch lists so
                            // the History "Incoming" section and the per-branch chips reflect the new commits.
                            repository.Refresh(CacheType.GitLog);
                            repository.Refresh(CacheType.Branches);
                        }
                        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                    })
                    .Start();
            }
            catch
            {
                fetching = false;
            }
        }
    }

    // The repository watcher only watches the .git directory, so working-tree changes made through
    // Unity (creating/saving/deleting assets) don't refresh the Changes/status view until a git
    // operation or a manual refresh. Unity already tells us when assets change, so use that to
    // refresh git status (and locks). Debounced, because OnPostprocessAllAssets fires often.
    class GitWorkingTreeRefresh : UnityEditor.AssetPostprocessor
    {
        private static double dueTime = -1;
        private static bool hooked;

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (importedAssets.Length == 0 && deletedAssets.Length == 0 && movedAssets.Length == 0)
                return;

            dueTime = EditorApplication.timeSinceStartup + 0.75; // coalesce bursts of imports
            if (!hooked)
            {
                EditorApplication.update += Tick;
                hooked = true;
            }
        }

        private static void Tick()
        {
            if (dueTime < 0 || EditorApplication.timeSinceStartup < dueTime)
                return;

            dueTime = -1;
            EditorApplication.update -= Tick;
            hooked = false;

            try
            {
                var manager = EntryPoint.ApplicationManager;
                var repository = manager != null && manager.Environment != null ? manager.Environment.Repository : null;
                if (repository != null)
                {
                    BackgroundGitRefresh.Mark();
                    repository.Refresh(CacheType.GitStatus);
                    repository.Refresh(CacheType.GitLocks);
                }
            }
            catch
            {
                // never let a refresh attempt break asset post-processing
            }
        }
    }
}
