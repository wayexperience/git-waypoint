#pragma warning disable 649
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Editor.Tasks;
using Unity.Editor.Tasks.Logging;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.VersionControl.Git.UI
{
    using IO;

    class LfsLocksModificationProcessor : UnityEditor.AssetModificationProcessor
    {
        private static ILogging Logger = LogHelper.GetLogger<LfsLocksModificationProcessor>();
        private static IRepository repository;
        private static IPlatform platform;
        private static IGitEnvironment environment;

        private static Dictionary<SPath, GitLock> locks = new Dictionary<SPath, GitLock>();
        private static CacheUpdateEvent lastLocksChangedEvent;
        // Single source of truth for our lock identity: ProjectWindowInterface resolves it (server-derived
        // via `git lfs locks --verify`, then config fallback) and refreshes it live, so the edit block here
        // and the badge colours there can never disagree.
        private static string loggedInUser => ProjectWindowInterface.CurrentUsername;

        // ---- Perforce-style auto-lock (acquire/release locks automatically) ----
        // Acquisition goes through repository.RequestLock/ReleaseLock so the lock cache - and the UI -
        // refresh immediately. The tracked/lockable checks and the release-on-close queries run on a
        // background thread (plain git) and marshal back to the main thread to touch the repository.

        private const string PrefEnabled = "gitAutoLock.enabled";
        private const string PrefOnSave = "gitAutoLock.onSave";
        private const string PrefOnOpen = "gitAutoLock.onOpenMode"; // 0 Off, 1 Auto, 2 Ask
        private const string PrefReleaseOnClose = "gitAutoLock.releaseOnClose";
        private const string PrefLockableExtensions = "gitAutoLock.lockableExtensions";

        public static bool AutoLockEnabled { get { return EditorPrefs.GetBool(PrefEnabled, true); } set { EditorPrefs.SetBool(PrefEnabled, value); } }
        public static bool LockOnSave { get { return EditorPrefs.GetBool(PrefOnSave, true); } set { EditorPrefs.SetBool(PrefOnSave, value); } }
        public static int OnOpenMode { get { return EditorPrefs.GetInt(PrefOnOpen, 1); } set { EditorPrefs.SetInt(PrefOnOpen, value); } }
        public static bool ReleaseOnClose { get { return EditorPrefs.GetBool(PrefReleaseOnClose, true); } set { EditorPrefs.SetBool(PrefReleaseOnClose, value); } }

        // Which file types are lockable is read from the repo's `.gitattributes` (`*.ext lockable` rules) -
        // the committed, team-shared policy - parsed once and cached until the file changes. There is no
        // per-user list: everyone locks the same files. (CheckAttrLockable is the authoritative per-file
        // confirmation; this fast set just avoids spawning git for obviously non-lockable saves.)
        private static HashSet<string> lockableExtensions;
        private static System.DateTime lockableAttrsStamp;
        private static string lockableAttrsPath;

        // MAIN THREAD ONLY (reads Application.dataPath via RepoDir). Set of extensions marked `lockable`.
        private static HashSet<string> LockableExtensions
        {
            get
            {
                try
                {
                    var path = System.IO.Path.Combine(RepoDir(), ".gitattributes");
                    var info = new System.IO.FileInfo(path);
                    if (!info.Exists)
                    {
                        lockableAttrsPath = path;
                        return lockableExtensions = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                    }
                    if (lockableExtensions != null && path == lockableAttrsPath && info.LastWriteTimeUtc == lockableAttrsStamp)
                        return lockableExtensions;
                    lockableAttrsPath = path;
                    lockableAttrsStamp = info.LastWriteTimeUtc;
                    lockableExtensions = ParseLockableExtensions(path);
                }
                catch
                {
                    if (lockableExtensions == null)
                        lockableExtensions = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                }
                return lockableExtensions;
            }
        }

        // Extract extensions from `*.ext lockable` lines in .gitattributes (ignores `-lockable` and non-glob patterns).
        private static HashSet<string> ParseLockableExtensions(string gitattributesPath)
        {
            var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var line in System.IO.File.ReadAllLines(gitattributesPath))
            {
                var l = line.Trim();
                if (l.Length == 0 || l[0] == '#' || l[0] == '[')
                    continue;
                var parts = l.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;
                bool lockable = false;
                for (int i = 1; i < parts.Length; i++)
                    if (parts[i] == "lockable") { lockable = true; break; }
                if (lockable && parts[0].StartsWith("*."))
                    set.Add("." + parts[0].Substring(2).ToLowerInvariant());
            }
            return set;
        }

        private static readonly HashSet<string> NonLockableExtensions = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".meta", ".txt", ".md", ".json", ".asmdef", ".asmref", ".shader", ".hlsl", ".cginc", ".uxml", ".uss", ".xml",
        };

        private static readonly object gate = new object();
        private static readonly HashSet<string> inFlight = new HashSet<string>();
        private static readonly Queue<System.Action> mainQueue = new Queue<System.Action>();
        private static bool hooked;

        public static void Initialize(IGitEnvironment env, IPlatform plat)
        {
            environment = env;
            platform = plat;

            repository = environment.Repository;
            UnityShim.Editor_finishedDefaultHeaderGUI += InspectorHeaderFinished;

            if (!hooked)
            {
                EditorApplication.update += DrainMainThread;
                EditorApplication.quitting += OnEditorQuitting;
                hooked = true;
            }

            if (repository != null)
            {
                repository.LocksChanged += RepositoryOnLocksChanged;
                repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitLocks, lastLocksChangedEvent);
            }
        }

        // ----------------- acquire -----------------

        public static string[] OnWillSaveAssets(string[] paths)
        {
            // Hard-block the save of files locked by someone else: drop them from the set Unity is about to
            // write. This is the reliable in-editor block (IsOpenForEdit alone doesn't stop a scene save).
            if (ApplicationConfiguration.BlockLockedByOthers)
            {
                var blocked = paths.Where(p => IsLockedBySomeoneElse(BaseAssetPath(p))).ToArray();
                if (blocked.Length > 0)
                {
                    paths = paths.Where(p => !IsLockedBySomeoneElse(BaseAssetPath(p))).ToArray();
                    var first = GetLock(BaseAssetPath(blocked[0]));
                    var who = first.HasValue ? first.Value.Owner.Name : "another user";
                    EditorUtility.DisplayDialog("Locked file not saved",
                        blocked.Length == 1
                            ? string.Format("\"{0}\" is locked by {1} and was not saved.", blocked[0], who)
                            : string.Format("{0} files are locked by other users and were not saved:\n\n{1}",
                                blocked.Length, string.Join("\n", blocked)),
                        "OK");
                }
            }

            if (AutoLockEnabled && LockOnSave)
            {
                foreach (var p in paths)
                    AcquireLock(p);
            }
            return paths;
        }

        // The .meta and its base asset share a lock; resolve a save/edit path to the base asset so a lock
        // on "Foo.unity" also covers "Foo.unity.meta". (Real suffix strip: TrimEnd would eat stray chars.)
        private static string BaseAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return assetPath;
            return assetPath.EndsWith(".meta") ? assetPath.Substring(0, assetPath.Length - ".meta".Length) : assetPath;
        }

        [OnOpenAsset]
        private static bool OnOpenAsset(int instanceID, int line)
        {
            if (!AutoLockEnabled)
                return false;

            int mode = OnOpenMode;
            if (mode == 0) // Off
                return false;

            // Unity 6000.5 made GetAssetPath(int) a hard error in favour of an EntityId overload that
            // doesn't exist on older Unity. int converts to EntityId implicitly, so cast on new Unity only.
#if UNITY_6000_5_OR_NEWER
            // int->EntityId itself is flagged for future removal, but OnOpenAsset only gives us an int.
#pragma warning disable 618
            string path = AssetDatabase.GetAssetPath((UnityEngine.EntityId)instanceID);
#pragma warning restore 618
#else
            string path = AssetDatabase.GetAssetPath(instanceID);
#endif
            if (string.IsNullOrEmpty(path))
                return false;

            // Warn before opening something locked by someone else: edits can't be saved (the save is
            // blocked and the file is read-only), so let them cancel instead of working for nothing.
            if (ApplicationConfiguration.BlockLockedByOthers && IsLockedBySomeoneElse(BaseAssetPath(path)))
            {
                var lck = GetLock(BaseAssetPath(path));
                var who = lck.HasValue ? lck.Value.Owner.Name : "another user";
                bool open = EditorUtility.DisplayDialog("File locked by " + who,
                    string.Format("\"{0}\" is locked by {1}. You can open it to look, but you won't be able to save changes.", path, who),
                    "Open read-only", "Cancel");
                return !open; // consume the open (block) when the user cancels
            }

            if (mode == 2) // Ask
            {
                string ext = System.IO.Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext) || NonLockableExtensions.Contains(ext) || !LockableExtensions.Contains(ext))
                    return false;
                if (GetLock(path) != null)
                    return false;

                int choice = EditorUtility.DisplayDialogComplex("Lock file?",
                    string.Format("Lock \"{0}\" so others can't edit it while you work?", path),
                    "Lock", "Skip", "Always lock");
                if (choice == 1)
                    return false;
                if (choice == 2)
                    OnOpenMode = 1; // Always -> Auto
            }

            AcquireLock(path);
            return false; // never consume the open
        }

        // Only lock files that are tracked by git (skip new/untracked) and of a lockable type,
        // and not already locked. The tracked/lockable check runs in the background; the actual
        // lock request runs on the main thread via the repository so the UI updates immediately.
        private static void AcquireLock(string assetPath)
        {
            if (repository == null || !AutoLockEnabled || string.IsNullOrEmpty(assetPath))
                return;

            assetPath = assetPath.Replace("\\", "/");
            string ext = System.IO.Path.GetExtension(assetPath);
            if (string.IsNullOrEmpty(ext) || NonLockableExtensions.Contains(ext))
                return;
            if (GetLock(assetPath) != null)
                return;

            lock (gate)
            {
                if (inFlight.Contains(assetPath))
                {
                    return;
                }
                inFlight.Add(assetPath);
            }

            // Show the lock instantly, before the background tracked/lockable git checks (two process
            // spawns, ~seconds). If those checks say it isn't lockable, the optimistic icon is reverted.
            ProjectWindowInterface.OptimisticLock(assetPath);

            string workingDir = RepoDir();
            Task.Run(() =>
            {
                string o, e;
                bool tracked = RunGit(new[] { "ls-files", "--error-unmatch", "--", assetPath }, workingDir, out o, out e) == 0;
                // Lockability is decided ONLY by the `lockable` rules in .gitattributes (committed, shared by
                // the whole team) - not a per-user list - so everyone locks the same files.
                bool lockable = tracked && CheckAttrLockable(assetPath, workingDir);
                Enqueue(() =>
                {
                    lock (gate) { inFlight.Remove(assetPath); }
                    if (lockable && repository != null && GetLock(assetPath) == null)
                    {
                        // Same single lock path the context menu and Locks panel use (optimistic + refresh).
                        ProjectWindowInterface.RequestLock(assetPath);
                    }
                    else
                    {
                        // Not lockable after all (untracked or not a lockable type): revert the optimistic icon.
                        ProjectWindowInterface.ClearOptimistic(assetPath);
                    }
                });
            });
        }

        // ----------------- enforcement + release -----------------

        public static AssetMoveResult OnWillMoveAsset(string oldPath, string newPath)
        {
            return IsLockedBySomeoneElse(oldPath) || IsLockedBySomeoneElse(newPath) ? AssetMoveResult.FailedMove : AssetMoveResult.DidNotMove;
        }

        public static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions option)
        {
            if (IsLockedBySomeoneElse(assetPath))
                return AssetDeleteResult.FailedDelete;

            // Deleting an asset you have locked: release your own lock (force, the file is going away).
            var lck = GetLock(assetPath);
            if (lck.HasValue && repository != null)
            {
                var sp = assetPath.ToSPath().RelativeToRepository(environment);
                repository.ReleaseLock(sp, lck.Value.ID, true).Start();
            }

            return AssetDeleteResult.DidNotDelete;
        }

        // Returns true if this file can be edited by this user
        public static bool IsOpenForEdit(string assetPath, out string message)
        {
            // A .meta and its asset travel together: a lock on the BASE asset must also block editing the
            // .meta. Resolve the base path (real suffix strip - TrimEnd takes a char SET, so ".meta" would
            // eat any trailing '.','m','e','t','a') and check the lock on the path itself AND its base.
            string basePath = assetPath.EndsWith(".meta")
                ? assetPath.Substring(0, assetPath.Length - ".meta".Length)
                : assetPath;

            var lck = GetLock(assetPath);
            if (!IsLockedBySomeoneElse(lck) && basePath != assetPath)
                lck = GetLock(basePath);

            if (ApplicationConfiguration.BlockLockedByOthers && IsLockedBySomeoneElse(lck))
            {
                message = string.Format("File is locked for editing by {0}", lck.Value.Owner.Name);
                return false;
            }

            // Optionally keep outdated files read-only until pulled, so you don't edit something that's
            // about to change on the server (which would just create a conflict).
            if (ApplicationConfiguration.BlockOutdatedEdit && IsOutdatedAsset(basePath))
            {
                message = "This file is outdated — a newer version is on the server. Pull before editing.";
                return false;
            }

            message = null;
            return true;
        }

        private static bool IsOutdatedAsset(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            return !string.IsNullOrEmpty(guid) && ProjectWindowInterface.IsOutdated(guid);
        }

        private static void OnEditorQuitting()
        {
            if (!AutoLockEnabled || !ReleaseOnClose || repository == null)
                return;

            // Release my locks on files with no local changes that are already in sync with origin.
            // On quit the repository's async tasks won't complete, so use plain git directly.
            string dir = RepoDir();
            string branch, e;
            if (RunGit(new[] { "rev-parse", "--abbrev-ref", "HEAD" }, dir, out branch, out e) != 0)
                return;
            branch = branch.Trim();

            foreach (var kv in new List<KeyValuePair<SPath, GitLock>>(locks))
            {
                var lck = kv.Value;
                if (!string.Equals(lck.Owner.Name, loggedInUser))
                    continue;

                string path = lck.Path.ToString();
                string status, diff, uo;
                RunGit(new[] { "status", "--porcelain", "--", path }, dir, out status, out e);
                if (!string.IsNullOrWhiteSpace(status))
                    continue;
                if (RunGit(new[] { "diff", "--quiet", "origin/" + branch, "--", path }, dir, out diff, out e) != 0)
                    continue;

                RunGit(new[] { "lfs", "unlock", path }, dir, out uo, out e);
            }
        }

        // Called after a successful push: release my locks on files whose changes are now pushed
        // (clean working tree and in sync with origin), so teammates can take them. A file I still
        // have open as a scene is left locked - releasing a lock on something you're still in is
        // surprising and not what "submit" should do.
        public static void ReleaseLocksAfterPush()
        {
            if (!AutoLockEnabled || repository == null)
                return;

            string dir = RepoDir();
            string branch, e;
            if (RunGit(new[] { "rev-parse", "--abbrev-ref", "HEAD" }, dir, out branch, out e) != 0)
                return;
            branch = branch.Trim();

            foreach (var kv in new List<KeyValuePair<SPath, GitLock>>(locks))
            {
                var lck = kv.Value;
                if (!string.Equals(lck.Owner.Name, loggedInUser))
                    continue;

                string path = lck.Path.ToString();
                if (IsSceneOpen(path))
                    continue;

                string status, diff;
                RunGit(new[] { "status", "--porcelain", "--", path }, dir, out status, out e);
                if (!string.IsNullOrWhiteSpace(status))
                    continue;
                if (RunGit(new[] { "diff", "--quiet", "origin/" + branch, "--", path }, dir, out diff, out e) != 0)
                    continue;

                // Release through the repository so the lock cache and project icons refresh. We have the
                // lock id, so pass it and let git-lfs skip the path->id server lookup.
                repository.ReleaseLock(lck.Path, lck.ID, false).Start();
            }

            repository.Refresh(CacheType.GitLocks);
        }

        private static bool IsSceneOpen(string repoRelativePath)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || string.IsNullOrEmpty(scene.path))
                    continue;
                var scenePath = scene.path.Replace("\\", "/");
                if (string.Equals(scenePath, repoRelativePath, System.StringComparison.OrdinalIgnoreCase)
                    || repoRelativePath.EndsWith("/" + scenePath, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Recompute which tracked files have a newer version on the remote we haven't pulled yet, so the
        // project window can badge them "outdated". Runs after a fetch/pull (the only times this changes).
        // Compares HEAD against the upstream's side since they diverged: git diff HEAD...@{u}.
        public static void RefreshOutdated()
        {
            if (repository == null)
                return;

            string dir = RepoDir();
            Task.Run(() =>
            {
                string o, e;
                var paths = new List<string>();
                if (RunGit(new[] { "-c", "core.quotepath=false", "diff", "--name-only", "HEAD...@{u}" }, dir, out o, out e) == 0 && !string.IsNullOrEmpty(o))
                {
                    foreach (var line in o.Split('\n'))
                    {
                        var p = line.Trim();
                        if (p.Length > 0)
                            paths.Add(p);
                    }
                }
                Enqueue(() => ProjectWindowInterface.SetOutdatedPaths(paths));
            });
        }

        // ----------------- helpers -----------------

        private static void Enqueue(System.Action a)
        {
            if (a == null) return;
            lock (gate) { mainQueue.Enqueue(a); }
        }

        private static void DrainMainThread()
        {
            while (true)
            {
                System.Action a = null;
                lock (gate) { if (mainQueue.Count > 0) a = mainQueue.Dequeue(); }
                if (a == null) break;
                a();
            }
        }

        private static string RepoDir()
        {
            return System.IO.Directory.GetParent(Application.dataPath).FullName;
        }

        private static bool CheckAttrLockable(string assetPath, string workingDir)
        {
            string o, e;
            if (RunGit(new[] { "check-attr", "lockable", "--", assetPath }, workingDir, out o, out e) == 0 && !string.IsNullOrEmpty(o))
                return o.TrimEnd().EndsWith(": set", System.StringComparison.OrdinalIgnoreCase);
            return false;
        }

        // Resolve the current user so our own locks are not treated as "locked by someone else".
        // The LFS lock owner on the server (e.g. a self-hosted login) does NOT necessarily match
        // git's user.name (a display name). Prefer an explicit `lfs.lockUser`, fall back to user.name.
        // (A fully server-authoritative option would be `git lfs locks --verify`.)
        private static string ResolveCurrentUser()
        {
            string user = RunGitConfig("lfs.lockUser");
            if (string.IsNullOrEmpty(user))
                user = RunGitConfig("user.name");
            return string.IsNullOrEmpty(user) ? null : user;
        }

        private static string RunGitConfig(string key)
        {
            string o, e;
            return RunGit(new[] { "config", "--get", key }, RepoDir(), out o, out e) == 0 ? (string.IsNullOrEmpty(o) ? null : o.Trim()) : null;
        }

        // Plain git invocation, safe off the main thread. Args are passed as discrete, unparsed entries
        // (ArgumentList) so paths with spaces/quotes/option-looking names can't break parsing - no manual
        // quoting. Prepends Homebrew paths so git/git-lfs resolve even when Unity is launched from
        // Finder/Hub with a minimal PATH.
        private static int RunGit(string[] args, string workingDir, out string stdout, out string stderr)
        {
            using (var p = new System.Diagnostics.Process())
            {
                p.StartInfo.FileName = "git";
                foreach (var a in args)
                    p.StartInfo.ArgumentList.Add(a);
                p.StartInfo.WorkingDirectory = workingDir;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                if (System.IO.Path.PathSeparator == ':')
                {
                    string current = p.StartInfo.EnvironmentVariables.ContainsKey("PATH")
                        ? p.StartInfo.EnvironmentVariables["PATH"]
                        : System.Environment.GetEnvironmentVariable("PATH");
                    p.StartInfo.EnvironmentVariables["PATH"] = "/opt/homebrew/bin:/usr/local/bin" + (string.IsNullOrEmpty(current) ? "" : (":" + current));
                }

                var outBuf = new System.Text.StringBuilder();
                var errBuf = new System.Text.StringBuilder();
                using (var outDone = new System.Threading.AutoResetEvent(false))
                using (var errDone = new System.Threading.AutoResetEvent(false))
                {
                    p.OutputDataReceived += (s, ev) => { if (ev.Data == null) outDone.Set(); else outBuf.AppendLine(ev.Data); };
                    p.ErrorDataReceived += (s, ev) => { if (ev.Data == null) errDone.Set(); else errBuf.AppendLine(ev.Data); };
                    try
                    {
                        p.Start();
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        const int timeout = 60000;
                        if (p.WaitForExit(timeout) && outDone.WaitOne(timeout) && errDone.WaitOne(timeout))
                        {
                            stdout = outBuf.ToString();
                            stderr = errBuf.ToString();
                            return p.ExitCode;
                        }
                        stdout = outBuf.ToString();
                        stderr = "git timed out";
                        try { p.Kill(); } catch { } // reap the hung child, else its pipe/socket FDs leak
                        return -1;
                    }
                    catch (System.Exception ex)
                    {
                        stdout = string.Empty;
                        stderr = ex.Message;
                        return -1;
                    }
                }
            }
        }

        private static void RepositoryOnLocksChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastLocksChangedEvent.Equals(cacheUpdateEvent))
            {
                lastLocksChangedEvent = cacheUpdateEvent;
                locks = repository.CurrentLocks.ToDictionary(gitLock => gitLock.Path);
                Enqueue(ReconcileReadOnly);
            }
        }

        // Called by the settings UI when the enforcement flag is toggled, so read-only is applied/cleared
        // immediately instead of waiting for the next lock refresh.
        public static void OnEnforcementSettingChanged() => Enqueue(ReconcileReadOnly);

        // Files we marked read-only, persisted (repo-relative) so a restart can still clear ones that were
        // unlocked while the editor was closed - otherwise they'd stay stuck read-only with no record of why.
        private const string PrefEnforcedReadOnly = "gitLock.enforcedReadOnly";
        private static string EnforcedPrefKey => PrefEnforcedReadOnly + ":" + RepoDir();

        private static HashSet<string> LoadEnforced()
        {
            var raw = EditorPrefs.GetString(EnforcedPrefKey, "");
            var set = new HashSet<string>();
            if (!string.IsNullOrEmpty(raw))
                foreach (var p in raw.Split('\n'))
                    if (p.Length > 0) set.Add(p);
            return set;
        }

        private static void SaveEnforced(HashSet<string> set) => EditorPrefs.SetString(EnforcedPrefKey, string.Join("\n", set));

        // Reconcile the read-only flag on disk so exactly the files locked by others are read-only. Only ever
        // touches files we set ourselves (tracked in EnforcedPrefKey), so a file the user made read-only by
        // hand is left alone. MAIN THREAD (reads loggedInUser / Application.dataPath).
        private static void ReconcileReadOnly()
        {
            var enforced = LoadEnforced();

            // Enforcement off (flag cleared, or identity not yet known): release everything we hold.
            if (!ApplicationConfiguration.BlockLockedByOthers || string.IsNullOrEmpty(loggedInUser))
            {
                foreach (var rel in enforced)
                    SetReadOnly(rel, false);
                SaveEnforced(new HashSet<string>());
                return;
            }

            var desired = new HashSet<string>();
            foreach (var kv in locks)
                if (IsLockedBySomeoneElse(kv.Value))
                    desired.Add(kv.Key.ToString());

            foreach (var rel in desired)
                if (!enforced.Contains(rel))
                    SetReadOnly(rel, true);
            foreach (var rel in enforced)
                if (!desired.Contains(rel))
                    SetReadOnly(rel, false);

            SaveEnforced(desired);
        }

        // Set/clear read-only on the asset file only (not its .meta: Unity rewrites .meta on import and a
        // read-only meta can break reimport). repoRelative is the lock path; resolve it against the repo root.
        private static void SetReadOnly(string repoRelative, bool readOnly)
        {
            try
            {
                var full = System.IO.Path.Combine(RepoDir(), repoRelative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!System.IO.File.Exists(full))
                    return;
                var attrs = System.IO.File.GetAttributes(full);
                var isReadOnly = (attrs & System.IO.FileAttributes.ReadOnly) != 0;
                if (readOnly && !isReadOnly)
                    System.IO.File.SetAttributes(full, attrs | System.IO.FileAttributes.ReadOnly);
                else if (!readOnly && isReadOnly)
                    System.IO.File.SetAttributes(full, attrs & ~System.IO.FileAttributes.ReadOnly);
            }
            catch (System.Exception ex)
            {
                Logger.Warning(ex, "Could not change read-only attribute on {0}", repoRelative);
            }
        }

        private static bool IsLockedBySomeoneElse(GitLock? lck)
        {
            return lck.HasValue && !lck.Value.Owner.Name.Equals(loggedInUser);
        }

        private static bool IsLockedBySomeoneElse(string assetPath)
        {
            return IsLockedBySomeoneElse(GetLock(assetPath));
        }

        private static GitLock? GetLock(string assetPath)
        {
            if (repository == null)
                return null;

            GitLock lck;
            var repositoryPath = assetPath.ToSPath().RelativeToRepository(environment);
            if (locks.TryGetValue(repositoryPath, out lck))
                return lck;
            return null;
        }

        private static void InspectorHeaderFinished(UnityEditor.Editor editor)
        {
            if (editor.target == null)
                return;

            var lck = GetLock(AssetDatabase.GetAssetPath(editor.target));
            if (!lck.HasValue)
                return;

            // Always show the lock status on the asset header, not just when it's locked by someone else:
            // an artist should be able to see at a glance whether they hold the lock or need to ask for it.
            bool mine = string.Equals(lck.Value.Owner.Name, loggedInUser);
            string message = mine ? "You have this file locked" : string.Format("Locked for editing by {0}", lck.Value.Owner.Name);
            var icon = mine ? Utility.GetIcon("lock-mine") : Utility.GetIcon("lock-other");

            var enabled = GUI.enabled;
            GUI.enabled = true;
            GUILayout.BeginVertical();
            {
                GUILayout.Space(9);
                GUILayout.BeginHorizontal();
                {
                    GUILayout.BeginVertical(GUILayout.Width(20));
                    {
                        GUILayout.Label(icon, GUILayout.Width(20), GUILayout.Height(20));
                    }
                    GUILayout.EndVertical();

                    GUILayout.BeginVertical();
                    {
                        GUILayout.Space(3);
                        GUILayout.Label(message, Styles.HeaderBranchLabelStyle);
                    }
                    GUILayout.EndVertical();
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            GUI.enabled = enabled;
        }
    }
}
