using System;
using System.Collections.Generic;
using System.Linq;
using SpoiledCat.Git;
using Unity.Editor.Tasks;
using Unity.Editor.Tasks.Logging;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.VersionControl.Git.UI
{
    using IO;

    class ProjectWindowInterface
    {
        private const string AssetsMenuRequestLock = "Assets/Request Lock";
        private const string AssetsMenuReleaseLock = "Assets/Release Lock";
        private const string AssetsMenuReleaseLockForced = "Assets/Release Lock (forced)";

        private static List<GitStatusEntry> entries = new List<GitStatusEntry>();
        private static List<GitLock> locks = new List<GitLock>();
        private static Dictionary<string, int> guids = new Dictionary<string, int>();
        private static List<string> guidsLocks = new List<string>();
        private static string currentUsername;
        internal static string CurrentUsername { get { return currentUsername; } }

        // Optimistic lock display: a file I just asked to lock/unlock is shown immediately, before the
        // git round-trip and lock-list refresh land, so the UI feels instant. These hold asset GUIDs and
        // are reconciled against the authoritative lock list in OnLocksUpdate (and cleared by the caller
        // if the real operation fails). We only ever optimistically lock our own files.
        private static readonly HashSet<string> optimisticLocks = new HashSet<string>();
        private static readonly HashSet<string> optimisticUnlocks = new HashSet<string>();
        // When each optimistic guess was made, so a request that never reports back (hung or a silent
        // failure) doesn't leave the file stuck on "Acquiring…/Releasing…" forever - see PruneStaleOptimistic.
        private static readonly Dictionary<string, double> optimisticSince = new Dictionary<string, double>();
        // Backstop derived from the configured git-command timeout (ms) rather than a magic number. A
        // lock/unlock plus its follow-up locks refresh are two git commands, each bounded by that
        // timeout, so anything still pending past 2x has really stalled.
        private static double OptimisticTimeoutSeconds => ApplicationConfiguration.GitTimeout / 1000.0 * 2;
        private static double nextOptimisticPrune;

        // guidsLocks is built in lockstep with the locks list (see OnLocksUpdate), so the index lines up.
        private static bool IsGuidLockedByMe(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return false;
            if (optimisticUnlocks.Contains(guid))
                return false;
            if (optimisticLocks.Contains(guid))
                return true;
            if (string.IsNullOrEmpty(currentUsername))
                return false;
            int i = guidsLocks.IndexOf(guid);
            return i >= 0 && i < locks.Count && locks[i].Owner.Name == currentUsername;
        }

        // Files that have a newer version on the remote we haven't pulled yet ("outdated"), by GUID.
        // Recomputed after a fetch/pull (see LfsLocksModificationProcessor.RefreshOutdated).
        private static readonly HashSet<string> outdatedGuids = new HashSet<string>();

        public static bool IsOutdated(string guid)
        {
            return !string.IsNullOrEmpty(guid) && outdatedGuids.Contains(guid);
        }

        public static void SetOutdatedPaths(List<string> repoPaths)
        {
            if (manager == null)
                return;
            var newSet = new HashSet<string>();
            foreach (var rp in repoPaths)
            {
                var assetPath = rp.ToSPath().RelativeToProject(manager.Environment);
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guid))
                    newSet.Add(guid);
            }
            if (newSet.SetEquals(outdatedGuids))
                return;
            outdatedGuids.Clear();
            outdatedGuids.UnionWith(newSet);
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        // The id of the lock on this repo path, if we know it, so unlock can pass --id and skip the lookup.
        private static string FindLockId(SPath repositoryPath)
        {
            foreach (var l in locks)
                if (l.Path == repositoryPath)
                    return l.ID;
            return null;
        }

        // The name of whoever holds the lock on this asset, or null if it isn't (server-)locked.
        // guidsLocks is built in lockstep with the locks list, so the index lines up.
        private static string GetLockOwnerForGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return null;
            int i = guidsLocks.IndexOf(guid);
            return i >= 0 && i < locks.Count ? locks[i].Owner.Name : null;
        }

        // Raised when an optimistic (in-flight) lock/unlock guess changes, so views that show pending
        // state - the Locks panel in particular - can rebuild immediately instead of waiting for the
        // server round-trip.
        public static event Action OptimisticChanged;

        private static void NotifyOptimisticChanged()
        {
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            var handler = OptimisticChanged;
            if (handler != null) handler();
        }

        public static void OptimisticLock(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
                return;
            optimisticUnlocks.Remove(guid);
            if (optimisticLocks.Add(guid))
            {
                optimisticSince[guid] = EditorApplication.timeSinceStartup;
                NotifyOptimisticChanged();
            }
        }

        public static void OptimisticUnlock(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
                return;
            optimisticLocks.Remove(guid);
            if (optimisticUnlocks.Add(guid))
            {
                optimisticSince[guid] = EditorApplication.timeSinceStartup;
                NotifyOptimisticChanged();
            }
        }

        // Revert an optimistic guess when the real lock/unlock failed.
        public static void ClearOptimistic(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
                return;
            bool changed = optimisticLocks.Remove(guid);
            changed |= optimisticUnlocks.Remove(guid);
            optimisticSince.Remove(guid);
            if (changed)
                NotifyOptimisticChanged();
        }

        // Safety net: revert any optimistic lock/unlock the server never confirmed (a hung or silently
        // failed request), so the file doesn't sit on "Acquiring…/Releasing…" indefinitely. The real
        // RequestLock/ReleaseLock continuation usually clears it first; this only catches the stragglers.
        private static void PruneStaleOptimistic()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < nextOptimisticPrune || optimisticSince.Count == 0)
                return;
            nextOptimisticPrune = now + 2;

            List<string> stale = null;
            foreach (var kv in optimisticSince)
            {
                if (now - kv.Value > OptimisticTimeoutSeconds)
                    (stale ?? (stale = new List<string>())).Add(kv.Key);
            }
            if (stale == null)
                return;

            foreach (var guid in stale)
            {
                optimisticLocks.Remove(guid);
                optimisticUnlocks.Remove(guid);
                optimisticSince.Remove(guid);
                Logger.Warning("Optimistic lock state for {0} not confirmed after {1}s; reverting.", guid, OptimisticTimeoutSeconds);
            }
            NotifyOptimisticChanged();
        }

        // Pending state for the Locks panel: a lock/unlock the user just triggered but the server
        // hasn't confirmed yet. Keyed by the project-relative asset path.
        public static bool IsPendingLock(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            return !string.IsNullOrEmpty(guid) && optimisticLocks.Contains(guid);
        }

        public static bool IsPendingUnlock(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            return !string.IsNullOrEmpty(guid) && optimisticUnlocks.Contains(guid);
        }

        // Asset paths the user asked to lock that aren't on the server yet - shown as "Acquiring…" rows.
        public static IEnumerable<string> PendingLockAssetPaths()
        {
            foreach (var guid in optimisticLocks)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath))
                    yield return assetPath;
            }
        }

        // THE single path for taking a lock. Everyone - auto-lock on open/save, the project context
        // menu, the Locks panel - goes through here so the behaviour is identical: show the lock
        // optimistically right away, send the request, then refresh the lock list on success or revert
        // the optimistic guess on failure. assetPath is the project-relative path (e.g. "Assets/x.mat").
        public static void RequestLock(string assetPath, Action<string> onError = null)
        {
            if (Repository == null || string.IsNullOrEmpty(assetPath))
                return;
            var repositoryPath = assetPath.ToSPath().RelativeToRepository(manager.Environment);
            OptimisticLock(assetPath);
            Repository.RequestLock(repositoryPath)
                .FinallyInUI((success, ex) =>
                {
                    // "already created lock" just means the lock we wanted already exists - that's the desired
                    // state, not an error to alarm the user with. Treat it as success: refresh and move on.
                    if (success || IsAlreadyLockedError(ex))
                        Repository.Refresh(CacheType.GitLocks);
                    else
                    {
                        ClearOptimistic(assetPath);
                        if (onError != null) onError(ex != null ? ex.Message : null);
                    }
                })
                .Start();
        }

        // THE single path for releasing a lock; mirror of RequestLock.
        public static void ReleaseLock(string assetPath, bool force, Action<string> onError = null)
        {
            if (Repository == null || string.IsNullOrEmpty(assetPath))
                return;
            var repositoryPath = assetPath.ToSPath().RelativeToRepository(manager.Environment);
            OptimisticUnlock(assetPath);
            // We already hold the lock id, so pass it: git-lfs then skips resolving the path to an id
            // against the server (a round-trip), roughly halving the unlock time.
            Repository.ReleaseLock(repositoryPath, FindLockId(repositoryPath), force)
                .FinallyInUI((success, ex) =>
                {
                    // Already unlocked? That's the state we wanted - treat it as success, no alarm.
                    if (success || IsAlreadyUnlockedError(ex))
                        Repository.Refresh(CacheType.GitLocks);
                    else
                    {
                        ClearOptimistic(assetPath);
                        if (onError != null) onError(ex != null ? ex.Message : null);
                    }
                })
                .Start();
        }

        // git-lfs reports an existing lock as "already created lock" - benign, the file is locked already.
        private static bool IsAlreadyLockedError(Exception ex)
        {
            var m = ex != null ? ex.Message : null;
            return !string.IsNullOrEmpty(m) && (m.IndexOf("already created lock", StringComparison.OrdinalIgnoreCase) >= 0
                                                || m.IndexOf("already locked", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Unlocking something that isn't locked is benign too - the file is already unlocked.
        private static bool IsAlreadyUnlockedError(Exception ex)
        {
            var m = ex != null ? ex.Message : null;
            return !string.IsNullOrEmpty(m) && (m.IndexOf("no lock", StringComparison.OrdinalIgnoreCase) >= 0
                                                || m.IndexOf("unable to find", StringComparison.OrdinalIgnoreCase) >= 0
                                                || m.IndexOf("not locked", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Shared error reporting for an interactive (menu-triggered) lock/unlock.
        private static void ReportLockError(string title, string error, string permissionMessage)
        {
            if (!string.IsNullOrEmpty(error) && error.Contains("exit status 255"))
                error = permissionMessage;
            EditorUtility.DisplayDialog(title, error ?? string.Empty, Localization.Ok);
        }

        private static IApplicationManager manager;
        private static bool isBusy = false;
        private static ILogging logger;
        private static ILogging Logger { get { return logger = logger ?? LogHelper.GetLogger<ProjectWindowInterface>(); } }
        private static CacheUpdateEvent lastRepositoryStatusChangedEvent;
        private static CacheUpdateEvent lastLocksChangedEvent;
        private static CacheUpdateEvent lastCurrentRemoteChangedEvent;
        private static IRepository Repository { get { return manager != null ? manager.Environment.Repository : null; } }
        private static IPlatform Platform { get { return manager != null ? manager.Platform : null; } }
        private static bool IsInitialized { get { return Repository != null; } }

        public static void Initialize(IApplicationManager theManager)
        {
            EditorApplication.projectWindowItemOnGUI -= OnProjectWindowItemGUI;
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
            EditorApplication.update -= PruneStaleOptimistic;
            EditorApplication.update += PruneStaleOptimistic;
            EditorApplication.update -= ApplyPendingUsername;
            EditorApplication.update += ApplyPendingUsername;

            manager = theManager;

            //Platform.Keychain.ConnectionsChanged += UpdateCurrentUsername;
            UpdateCurrentUsername();

            if (IsInitialized)
            {
                Repository.StatusEntriesChanged += RepositoryOnStatusEntriesChanged;
                Repository.LocksChanged += RepositoryOnLocksChanged;
                Repository.CurrentRemoteChanged += RepositoryOnCurrentRemoteChanged;
                ValidateCachedData();
            }
        }

        // What to draw on a file: a coloured square badge carrying either a letter (diff status, matching
        // the Changes list) or a white glyph (a padlock for locks). HasValue=false => nothing.
        public struct StatusBadge
        {
            public bool HasValue;
            public Color Color;
            public string Letter;   // a letter/symbol drawn white on the square (M/A/D/R/C/↓)
            public Texture2D Glyph; // a white glyph drawn on the square instead of a letter (padlock)
        }

        // Single source of truth for a file's overlay badge, shared by the project and hierarchy windows.
        // Every badge is the same coloured square as the Changes list. Priority: a merge conflict wins over
        // "outdated", which wins over a lock, which wins over a plain working-tree change.
        public static StatusBadge GetBadgeForAssetGUID(string guid)
        {
            // A file can hold an LFS lock without any working-tree change (e.g. auto-locked on open).
            // Don't bail when there's no status entry - fall through so a lock-only file still gets a badge.
            if (!guids.TryGetValue(guid, out int index))
                index = -1;

            var indexLock = guidsLocks.IndexOf(guid);
            bool optimisticallyLocked = optimisticLocks.Contains(guid);
            bool optimisticallyUnlocked = optimisticUnlocks.Contains(guid);

            if (index < 0 && indexLock < 0 && !optimisticallyLocked && !IsOutdated(guid))
                return default;

            GitFileStatus status = GitFileStatus.None;
            if (index >= 0 && index < entries.Count)
                status = entries[index].Status;

            var isLocked = (indexLock >= 0 || optimisticallyLocked) && !optimisticallyUnlocked;

            // Conflict (a "C" letter) wins; then outdated (amber, down-arrow); then a lock (padlock glyph,
            // green when mine / red when someone else's); otherwise the plain diff letter.
            if (status != GitFileStatus.Unmerged)
            {
                if (IsOutdated(guid))
                    return new StatusBadge { HasValue = true, Color = GitWaypointTheme.Outdated, Letter = "↓" };
                if (isLocked)
                {
                    var col = IsGuidLockedByMe(guid) ? GitWaypointTheme.UpToDate : GitWaypointTheme.Conflict;
                    return new StatusBadge { HasValue = true, Color = col, Glyph = Utility.GetIcon("lock-light") };
                }
            }

            GitWaypointTheme.DiffBadge(status, out string letter, out Color color);
            return new StatusBadge { HasValue = true, Letter = letter, Color = color };
        }

        // Draws a resolved badge into rect during a Repaint event. Used by both overlay windows.
        public static void DrawBadge(Rect rect, StatusBadge badge)
        {
            if (!badge.HasValue)
                return;
            GitWaypointTheme.DrawSquareBadge(rect, badge.Color, badge.Letter, badge.Glyph);
        }

        private static bool EnsureInitialized()
        {
            if (locks == null)
                locks = new List<GitLock>();
            if (entries == null)
                entries = new List<GitStatusEntry>();
            if (guids == null)
                guids = new Dictionary<string, int>();
            if (guidsLocks == null)
                guidsLocks = new List<string>();
            return IsInitialized;
        }

        private static void ValidateCachedData()
        {
            Repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitStatus, lastRepositoryStatusChangedEvent);
            Repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitLocks, lastLocksChangedEvent);
        }

        private static void RepositoryOnStatusEntriesChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastRepositoryStatusChangedEvent.Equals(cacheUpdateEvent))
            {
                lastRepositoryStatusChangedEvent = cacheUpdateEvent;
                entries.Clear();
                entries.AddRange(Repository.CurrentChanges);
                OnStatusUpdate();
            }
        }

        private static void RepositoryOnLocksChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastLocksChangedEvent.Equals(cacheUpdateEvent))
            {
                lastLocksChangedEvent = cacheUpdateEvent;
                locks = Repository.CurrentLocks;
                OnLocksUpdate();
                RefreshCurrentUsernameAsync(); // re-read identity so lock ownership reflects current lfs.lockUser
            }
        }

        private static void RepositoryOnCurrentRemoteChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastCurrentRemoteChangedEvent.Equals(cacheUpdateEvent))
            {
                lastCurrentRemoteChangedEvent = cacheUpdateEvent;
            }
        }

        // The single source of truth for "who am I" on locks, used by both the badges here and the edit
        // block in LfsLocksModificationProcessor. Authoritative and zero-config: ask the server which locks
        // are "ours" (`git lfs locks --verify`) - the owner name it puts on one of them is exactly how the
        // server addresses us, so the later owner-name comparison is exact (no display-name guessing).
        // The init value comes from cache/config instantly; the server confirmation lands asynchronously.

        private static string IdentityPrefKey(string repoDir)
        {
            return "waygit.lockIdentity:" + (repoDir ?? "");
        }

        // Returns the owner name the server reports on one of OUR locks, or null. A server round-trip, so
        // only call off the main thread; failures (offline, no git-lfs) return null and let callers fall back.
        private static string ReadOwnLockName(string repoDir)
        {
            var json = RunGit(repoDir, "lfs locks --verify --json", 8000);
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                var parsed = JsonUtility.FromJson<LfsLocksVerify>(json);
                if (parsed != null && parsed.ours != null && parsed.ours.Length > 0 && parsed.ours[0].owner != null)
                    return parsed.ours[0].owner.name;
            }
            catch { }
            return null;
        }

        // Pure git invocation with a timeout (no Unity APIs), safe off the main thread.
        private static string RunGit(string repoDir, string args, int timeoutMs)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = repoDir,
                };
                if (System.IO.Path.PathSeparator == ':')
                {
                    string current = psi.EnvironmentVariables.ContainsKey("PATH")
                        ? psi.EnvironmentVariables["PATH"]
                        : System.Environment.GetEnvironmentVariable("PATH");
                    psi.EnvironmentVariables["PATH"] = "/opt/homebrew/bin:/usr/local/bin" + (string.IsNullOrEmpty(current) ? "" : (":" + current));
                }
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    // Drain BOTH streams and kill the child on timeout (see ReadGitConfig): this is a network
                    // call (lfs locks --verify), exactly where a hung git/ssh would otherwise leak its
                    // socket + pipes every poll.
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                    p.WaitForExit();
                    string output = outTask.GetAwaiter().GetResult();
                    errTask.GetAwaiter().GetResult();
                    return output;
                }
            }
            catch
            {
                return null;
            }
        }

        // Init (main thread): set the identity instantly from the cached server name or local git config -
        // NO network here - then confirm with the server asynchronously so startup never blocks.
        private static void UpdateCurrentUsername()
        {
            if (Repository == null) { currentUsername = String.Empty; return; }
            currentUsername = ResolveLockUserLocal(RepoDir());
            RefreshCurrentUsernameAsync();
        }

        // Instant, no server round-trip: last server-confirmed name, else local git config.
        private static string ResolveLockUserLocal(string repoDir)
        {
            var cached = EditorPrefs.GetString(IdentityPrefKey(repoDir), null);
            if (!string.IsNullOrEmpty(cached))
                return cached;
            var username = ReadGitConfig(repoDir, "lfs.lockUser");
            if (string.IsNullOrEmpty(username))
                username = ReadGitConfig(repoDir, "user.name");
            return username ?? String.Empty;
        }

        // Confirm the identity with the server off the main thread. We only UPDATE when the server gives an
        // answer (a lock it says is ours); when it can't (offline, or you hold no locks yet) we leave the
        // current value untouched - so going offline never clobbers the known identity with a local guess.
        private static volatile string pendingUsername;
        private static volatile bool hasPendingUsername;

        private static void RefreshCurrentUsernameAsync()
        {
            if (Repository == null)
                return;
            string dir = RepoDir(); // capture on the main thread (no Unity APIs run off-thread below)
            // Once the server has confirmed our name (cached), stop hitting the network: the identity doesn't
            // change, so re-running `git lfs locks --verify` on every 10s poll would just be wasted (and once
            // leaky) round-trips.
            if (!string.IsNullOrEmpty(EditorPrefs.GetString(IdentityPrefKey(dir), null)))
                return;
            System.Threading.Tasks.Task.Run(() =>
            {
                var server = ReadOwnLockName(dir); // pure git; null if offline or no own locks
                if (!string.IsNullOrEmpty(server))
                {
                    pendingUsername = server;
                    hasPendingUsername = true; // set value before flag so the reader sees a consistent pair
                }
            });
        }

        // Runs on EditorApplication.update (main thread): applies a server-confirmed identity and remembers
        // it for offline use. EditorPrefs/RepaintAllViews are main-thread-only, hence done here.
        private static void ApplyPendingUsername()
        {
            if (!hasPendingUsername)
                return;
            hasPendingUsername = false;
            var u = pendingUsername ?? String.Empty;
            if (string.IsNullOrEmpty(u))
                return;
            EditorPrefs.SetString(IdentityPrefKey(RepoDir()), u);
            if (!string.Equals(currentUsername, u, StringComparison.Ordinal))
            {
                currentUsername = u;
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
        }

        // The working-tree directory. Touches Application.dataPath, so MAIN THREAD ONLY - capture it before
        // dispatching any background work.
        private static string RepoDir()
        {
            return System.IO.Directory.GetParent(Application.dataPath).FullName;
        }

        // Pure git invocation (no Unity APIs), safe to call off the main thread.
        private static string ReadGitConfig(string repoDir, string key)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", "config --get " + key)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = repoDir,
                };
                if (System.IO.Path.PathSeparator == ':')
                {
                    string current = psi.EnvironmentVariables.ContainsKey("PATH")
                        ? psi.EnvironmentVariables["PATH"]
                        : System.Environment.GetEnvironmentVariable("PATH");
                    psi.EnvironmentVariables["PATH"] = "/opt/homebrew/bin:/usr/local/bin" + (string.IsNullOrEmpty(current) ? "" : (":" + current));
                }
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    // Drain BOTH stdout and stderr (a redirected-but-unread stderr can fill its pipe buffer and
                    // wedge the child) and KILL the child if it overruns the timeout - otherwise the `using`
                    // disposes our wrapper while the git/ssh child lives on, leaking its pipe/socket FDs.
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } }
                    p.WaitForExit();
                    string output = outTask.GetAwaiter().GetResult();
                    errTask.GetAwaiter().GetResult();
                    return string.IsNullOrEmpty(output) ? null : output.Trim();
                }
            }
            catch
            {
                return null;
            }
        }

        [MenuItem(AssetsMenuRequestLock, true, 10000)]
        private static bool ContextMenu_CanLock()
        {
            if (!EnsureInitialized())
                return false;
            if (!Repository.CurrentRemote.HasValue)
                return false;
            if (isBusy)
                return false;
            return Selection.objects.Any(IsObjectUnlocked);
        }

        [MenuItem(AssetsMenuReleaseLock, true, 10001)]
        private static bool ContextMenu_CanUnlock()
        {
            if (!EnsureInitialized())
                return false;
            if (!Repository.CurrentRemote.HasValue)
                return false;
            if (isBusy)
                return false;
            return Selection.objects.Any(f => IsObjectLocked(f , true));
        }

        [MenuItem(AssetsMenuReleaseLockForced, true, 10002)]
        private static bool ContextMenu_CanUnlockForce()
        {
            if (!EnsureInitialized())
                return false;
            if (!Repository.CurrentRemote.HasValue)
                return false;
            if (isBusy)
                return false;
            return Selection.objects.Any(IsObjectLocked);
        }

        [MenuItem(AssetsMenuRequestLock, false, 10000)]
        private static void ContextMenu_Lock()
        {
            foreach (var selected in Selection.objects.Where(IsObjectUnlocked))
                RequestLock(AssetDatabase.GetAssetPath(selected),
                    error => ReportLockError(Localization.RequestLockActionTitle, error, "Failed to lock: no permissions"));
        }

        [MenuItem(AssetsMenuReleaseLock, false, 10001)]
        private static void ContextMenu_Unlock()
        {
            foreach (var selected in Selection.objects.Where(IsObjectLocked))
                ReleaseLock(AssetDatabase.GetAssetPath(selected), false,
                    error => ReportLockError(Localization.ReleaseLockActionTitle, error, "Failed to unlock: no permissions"));
        }

        [MenuItem(AssetsMenuReleaseLockForced, false, 10002)]
        private static void ContextMenu_UnlockForce()
        {
            foreach (var selected in Selection.objects.Where(IsObjectLocked))
                ReleaseLock(AssetDatabase.GetAssetPath(selected), true,
                    error => ReportLockError(Localization.ReleaseLockActionTitle, error, "Failed to unlock: no permissions"));
        }

        private static bool IsObjectUnlocked(Object selected)
        {
            if (selected == null)
                return false;

            SPath assetPath = AssetDatabase.GetAssetPath(selected).ToSPath();
            SPath repositoryPath = assetPath.RelativeToRepository(manager.Environment);

            var alreadyLocked = locks.Any(x => repositoryPath == x.Path);
            if (alreadyLocked)
                return false;

            GitFileStatus status = GitFileStatus.None;
            if (entries != null)
            {
                status = entries.FirstOrDefault(x => repositoryPath == x.Path.ToSPath()).Status;
            }
            return status != GitFileStatus.Untracked && status != GitFileStatus.Ignored;
        }

        private static bool IsObjectLocked(Object selected)
        {
            return IsObjectLocked(selected, false);
        }

        private static bool IsObjectLocked(Object selected, bool isLockedByCurrentUser)
        {
            if (selected == null)
                return false;

            SPath assetPath = AssetDatabase.GetAssetPath(selected).ToSPath();
            SPath repositoryPath = assetPath.RelativeToRepository(manager.Environment);

            return locks.Any(x => repositoryPath == x.Path && (!isLockedByCurrentUser || x.Owner.Name == currentUsername));
        }

        private static void OnLocksUpdate()
        {
            var newGuidsLocks = new List<string>();
            foreach (var lck in locks)
            {
                SPath repositoryPath = lck.Path;
                SPath assetPath = repositoryPath.RelativeToProject(manager.Environment);
                newGuidsLocks.Add(AssetDatabase.AssetPathToGUID(assetPath));
            }

            bool changed = !newGuidsLocks.SequenceEqual(guidsLocks);
            guidsLocks = newGuidsLocks;

            // The authoritative list just arrived: drop optimistic guesses it now reflects (a predicted
            // lock that showed up, or a predicted unlock whose file is gone), keeping the rest in flight.
            int optCount = optimisticLocks.Count + optimisticUnlocks.Count;
            optimisticLocks.RemoveWhere(g => guidsLocks.Contains(g));
            optimisticUnlocks.RemoveWhere(g => !guidsLocks.Contains(g));
            changed |= optimisticLocks.Count + optimisticUnlocks.Count != optCount;
            // Forget timeout timestamps for guesses the server just settled.
            if (optimisticSince.Count > 0)
                foreach (var g in new List<string>(optimisticSince.Keys))
                    if (!optimisticLocks.Contains(g) && !optimisticUnlocks.Contains(g))
                        optimisticSince.Remove(g);

            // Repaint everything (project window + inspector, so "locked by X" / IsOpenForEdit update) only
            // when the lock set actually changed - otherwise the periodic poll repaints every tick for nothing.
            if (changed)
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private static void OnStatusUpdate()
        {
            // Rebuild from scratch: otherwise a GUID from a previous status keeps a stale index that now
            // points at a different entry, drawing the wrong status icon on an unrelated (e.g. committed) file.
            guids.Clear();
            for (var index = 0; index < entries.Count; ++index)
            {
                var guid = AssetDatabase.AssetPathToGUID(entries[index].ProjectPath);
                guids[guid] = index;
            }

            AssetDatabase.Refresh();
        }

        private static void OnProjectWindowItemGUI(string guid, Rect itemRect)
        {
            if (!EnsureInitialized())
                return;

            if (!ApplicationConfiguration.ProjectIconsEnabled)
                return;

            if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(guid))
                return;

            StatusBadge badge = GetBadgeForAssetGUID(guid);
            if (!badge.HasValue)
                return;

            // Nominal badge size; clamped to the row below so it stays square and never crowds the row.
            const float badgeSize = 20f;
            Rect rect;

            // End of row placement: a small SQUARE badge, vertically centred, a touch smaller than the row
            // so badges on adjacent rows keep a gap and don't touch.
            if (itemRect.width > itemRect.height)
            {
                float size = Mathf.Min(badgeSize, itemRect.height) - 3f;
                rect = new Rect(itemRect.xMax - size - 2f, itemRect.y + (itemRect.height - size) * 0.5f, size, size);
            }
            // Corner placement
            // TODO: Magic numbers that need reviewing. Make sure this works properly with long filenames and wordwrap.
            else
            {
                var scale = itemRect.height / 90f;
                var size = new Vector2(badgeSize * scale, badgeSize * scale);
                size = size / EditorGUIUtility.pixelsPerPoint;
                var offset = new Vector2(itemRect.width * Mathf.Min(.4f * scale, .2f), itemRect.height * Mathf.Min(.2f * scale, .2f));
                rect = new Rect(itemRect.center.x - size.x * .5f + offset.x, itemRect.center.y - size.y * .5f + offset.y, size.x, size.y);
            }

            DrawBadge(rect, badge);

            // Hovering the badge explains the state (outdated / who holds the lock), so artists can act
            // without opening a panel. The label has no visible content; it only carries the tooltip.
            string tooltip = null;
            if (IsOutdated(guid))
                tooltip = "Newer version on the server - pull to update";
            var owner = GetLockOwnerForGuid(guid);
            if (!string.IsNullOrEmpty(owner))
            {
                var lockTip = owner == currentUsername ? "Locked by you" : "Locked by " + owner;
                tooltip = tooltip == null ? lockTip : tooltip + "\n" + lockTip;
            }
            if (tooltip != null)
                GUI.Label(rect, new GUIContent(string.Empty, tooltip));
        }
    }

    // Shape of `git lfs locks --verify --json` for JsonUtility.
    [Serializable] class LfsLockOwner { public string name; }
    [Serializable] class LfsLockEntry { public string id; public string path; public LfsLockOwner owner; }
    [Serializable] class LfsLocksVerify { public LfsLockEntry[] ours; public LfsLockEntry[] theirs; }
}
