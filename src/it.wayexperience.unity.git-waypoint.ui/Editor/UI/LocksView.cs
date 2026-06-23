#pragma warning disable 649,414
using System;
using System.Collections.Generic;
using System.Linq;
using SpoiledCat.Git;
using Unity.Editor.Tasks;
using UnityEditor;
using UnityEngine;

namespace Unity.VersionControl.Git.UI
{
    using IO;

    [Serializable]
    public class GitLockEntryDictionary : SerializableDictionary<string, GitLockEntry> { }

    [Serializable]
    public class GitStatusDictionary : SerializableDictionary<string, GitStatus> { }

    // A lock/unlock the user just triggered, shown in the panel before the server confirms.
    public enum LockPending { None, Locking, Unlocking }

    [Serializable]
    public class GitLockEntry
    {
        public static GitLockEntry Default = new GitLockEntry(GitLock.Default, GitFileStatus.None);

        [NonSerialized] public Texture Icon;
        [NonSerialized] public Texture IconBadge;

        [SerializeField] private GitLock gitLock;
        [SerializeField] private GitFileStatus gitFileStatus;
        [SerializeField] private string lockedAt;
        [SerializeField] private LockPending pending;

        public LockPending Pending { get { return pending; } set { pending = value; } }

        public GitLockEntry(GitLock gitLock, GitFileStatus gitFileStatus)
        {
            this.gitLock = gitLock;
            this.gitFileStatus = gitFileStatus;
            this.lockedAt = gitLock.LockedAt.ToLocalTime().CreateRelativeTime(DateTimeOffset.Now);
        }

        public GitLock GitLock
        {
            get { return gitLock; }
        }

        public GitFileStatus GitFileStatus
        {
            get { return gitFileStatus; }
        }

        public string LockedAt { get { return lockedAt; } }
    }

    [Serializable]
    class LocksControl
    {
        [NonSerialized] private Action<GitLock> rightClickNextRender;
        [NonSerialized] private GitLockEntry rightClickNextRenderEntry;
        [NonSerialized] private int controlId;
        [NonSerialized] private UnityEngine.Object lastActivatedObject;
        [NonSerialized] private Dictionary<string, bool> visibleItems = new Dictionary<string, bool>();

        [SerializeField] private Vector2 scroll;
        [SerializeField] private List<GitLockEntry> gitLockEntries = new List<GitLockEntry>();
        [SerializeField] public GitLockEntryDictionary assets = new GitLockEntryDictionary();
        [SerializeField] public GitStatusDictionary gitStatusDictionary = new GitStatusDictionary();
        [SerializeField] private GitLockEntry selectedEntry;
        [SerializeField] public SPath projectPath;

        public bool IsEmpty { get { return gitLockEntries.Count == 0; } }

        public GitLockEntry SelectedEntry
        {
            get
            {
                return selectedEntry;
            }
            set
            {
                selectedEntry = value;

                var activeObject = selectedEntry != null && selectedEntry.GitLock != GitLock.Default && projectPath.IsInitialized
                    ? AssetDatabase.LoadMainAssetAtPath(selectedEntry.GitLock.Path.MakeAbsolute().RelativeTo(projectPath))
                    : null;

                lastActivatedObject = activeObject;

                if (LocksControlHasFocus)
                {
                    Selection.activeObject = activeObject;
                }
            }
        }

        public bool Render(Rect containingRect, Action<GitLock> singleClick = null,
            Action<GitLock> doubleClick = null, Action<GitLock> rightClick = null)
        {
            var requiresRepaint = false;
            scroll = GUILayout.BeginScrollView(scroll);
            {
                controlId = GUIUtility.GetControlID(FocusType.Keyboard);

                if (Event.current.type != EventType.Repaint)
                {
                    if (rightClickNextRender != null)
                    {
                        rightClickNextRender.Invoke(rightClickNextRenderEntry.GitLock);
                        rightClickNextRender = null;
                        rightClickNextRenderEntry = GitLockEntry.Default;
                    }
                }

                var startDisplay = scroll.y;
                var endDisplay = scroll.y + containingRect.height;

                var rect = new Rect(containingRect.x, containingRect.y, containingRect.width, 0);
                for (var index = 0; index < gitLockEntries.Count; index++)
                {
                    var entry = gitLockEntries[index];

                    var entryRect = new Rect(rect.x, rect.y, rect.width, Styles.LocksEntryHeight);

                    if (Event.current.type == EventType.Layout)
                    {
                        var shouldRenderEntry = !(entryRect.y > endDisplay || entryRect.yMax < startDisplay);
                        visibleItems[entry.GitLock.ID] = shouldRenderEntry;
                    }

                    if (visibleItems.ContainsKey(entry.GitLock.ID) && visibleItems[entry.GitLock.ID])
                    {
                        entryRect = RenderEntry(entryRect, entry);
                    }

                    var entryRequiresRepaint =
                        HandleInput(entryRect, entry, index, singleClick, doubleClick, rightClick);
                    requiresRepaint = requiresRepaint || entryRequiresRepaint;

                    rect.y += entryRect.height;
                }

                GUILayout.Space(rect.y - containingRect.y);
            }
            GUILayout.EndScrollView();

            return requiresRepaint;
        }

        private Rect RenderEntry(Rect entryRect, GitLockEntry entry)
        {
            var isSelected = entry == SelectedEntry;
            var iconWidth = 32;
            var iconHeight = 32;
            var iconBadgeWidth = 16;
            var iconBadgeHeight = 16;
            var hasKeyboardFocus = GUIUtility.keyboardControl == controlId;

            GUILayout.BeginHorizontal(isSelected ? Styles.SelectedArea : Styles.Label);
            GUILayout.Label(entry.Icon, GUILayout.Height(iconWidth), GUILayout.Width(iconHeight));
            if (Event.current.type == EventType.Repaint)
            {
                var iconRect = GUILayoutUtility.GetLastRect();
                var iconBadgeRect = new Rect(iconRect.x + iconBadgeWidth, iconRect.y + iconBadgeHeight, iconBadgeWidth, iconBadgeHeight);
                Styles.Label.Draw(iconBadgeRect, entry.IconBadge, false, false, false, hasKeyboardFocus);
            }
            string status;
            if (entry.Pending == LockPending.Locking)
                status = "Acquiring lock…";
            else if (entry.Pending == LockPending.Unlocking)
                status = "Releasing lock…";
            else
                status = string.Format("Locked {0} by {1}", entry.LockedAt, entry.GitLock.Owner.Name);

            GUILayout.BeginVertical();
            GUILayout.Label(entry.GitLock.Path, isSelected ? Styles.SelectedLabel : Styles.Label);
            GUILayout.Label(status, isSelected ? Styles.LocksViewLockedBySelectedStyle : Styles.LocksViewLockedByStyle);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            var itemRect = GUILayoutUtility.GetLastRect();
            return itemRect;
        }

        private bool HandleInput(Rect rect, GitLockEntry entry, int index, Action<GitLock> singleClick = null,
            Action<GitLock> doubleClick = null, Action<GitLock> rightClick = null)
        {
            var requiresRepaint = false;
            var clickRect = new Rect(0f, rect.y, rect.width, rect.height);
            if (Event.current.type == EventType.MouseDown && clickRect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
                GUIUtility.keyboardControl = controlId;

                SelectedEntry = entry;
                requiresRepaint = true;
                var clickCount = Event.current.clickCount;
                var mouseButton = Event.current.button;

                if (mouseButton == 0 && clickCount == 1 && singleClick != null)
                {
                    singleClick(entry.GitLock);
                }
                if (mouseButton == 0 && clickCount > 1 && doubleClick != null)
                {
                    doubleClick(entry.GitLock);
                }
                if (mouseButton == 1 && clickCount == 1 && rightClick != null)
                {
                    rightClickNextRender = rightClick;
                    rightClickNextRenderEntry = entry;
                }
            }

            // Keyboard navigation if this child is the current selection
            if (GUIUtility.keyboardControl == controlId && entry == SelectedEntry && Event.current.type == EventType.KeyDown)
            {
                var directionY = Event.current.keyCode == KeyCode.UpArrow ? -1 : Event.current.keyCode == KeyCode.DownArrow ? 1 : 0;
                if (directionY != 0)
                {
                    Event.current.Use();

                    if (directionY > 0)
                    {
                        requiresRepaint = SelectNext(index);
                    }
                    else
                    {
                        requiresRepaint = SelectPrevious(index);
                    }
                }
            }

            return requiresRepaint;
        }

        public void Load(List<GitLock> locks, List<GitStatusEntry> gitStatusEntries)
        {
            var statusEntries = new Dictionary<string, int>();
            for (int i = 0; i < gitStatusEntries.Count; i++)
                statusEntries.Add(gitStatusEntries[i].Path.ToSPath().ToString(SlashMode.Forward), i);
            var selectedLockId = SelectedEntry != null && SelectedEntry.GitLock != GitLock.Default
                ? SelectedEntry.GitLock.ID
                : null;

            var scrollValue = scroll.y;
            var previousCount = gitLockEntries.Count;
            var scrollIndex = (int)(scrollValue / Styles.LocksEntryHeight);

            assets.Clear();
            visibleItems.Clear();

            gitLockEntries = locks.Select(gitLock =>
            {
                int index = -1;
                GitFileStatus gitFileStatus = GitFileStatus.None;
                if (statusEntries.TryGetValue(gitLock.Path.ToString(SlashMode.Forward), out index))
                {
                    gitFileStatus = gitStatusEntries[index].Status;
                }

                var gitLockEntry = new GitLockEntry(gitLock, gitFileStatus);
                LoadIcon(gitLockEntry, true);
                var path = gitLock.Path.MakeAbsolute().RelativeTo(projectPath);
                var assetPathString = path.ToString();
                if (ProjectWindowInterface.IsPendingUnlock(assetPathString))
                    gitLockEntry.Pending = LockPending.Unlocking;
                else if (ProjectWindowInterface.IsPendingLock(assetPathString))
                    gitLockEntry.Pending = LockPending.Locking;
                var assetGuid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(assetGuid))
                {
                    assets.Add(assetGuid, gitLockEntry);
                }

                visibleItems.Add(gitLockEntry.GitLock.ID, false);
                return gitLockEntry;
            }).ToList();

            var selectionPresent = false;
            for (var index = 0; index < gitLockEntries.Count; index++)
            {
                var gitLockEntry = gitLockEntries[index];
                if (selectedLockId == gitLockEntry.GitLock.ID)
                {
                    selectedEntry = gitLockEntry;
                    selectionPresent = true;
                    break;
                }
            }

            if (!selectionPresent)
            {
                selectedEntry = GitLockEntry.Default;
            }

            if (scrollIndex > gitLockEntries.Count)
            {
                ScrollTo(0);
            }
            else
            {
                var scrollOffset = scrollValue % Styles.LocksEntryHeight;

                var scrollIndexFromBottom = previousCount - scrollIndex;
                var newScrollIndex = gitLockEntries.Count - scrollIndexFromBottom;

                ScrollTo(newScrollIndex, scrollOffset);
            }
        }

        public void LoadIcons()
        {
            foreach (var gitLockEntry in gitLockEntries)
            {
                LoadIcon(gitLockEntry);
            }
        }

        private void LoadIcon(GitLockEntry gitLockEntry, bool force = false)
        {
            if (force || gitLockEntry.Icon == null)
            {
                gitLockEntry.Icon = GetNodeIcon(gitLockEntry.GitLock);
            }

            if (force || gitLockEntry.IconBadge == null)
            {
                var me = ProjectWindowInterface.CurrentUsername;
                bool mine = !string.IsNullOrEmpty(me) && gitLockEntry.GitLock.Owner.Name == me;
                gitLockEntry.IconBadge = Styles.GetLockBadge(gitLockEntry.GitFileStatus, mine);
            }
        }

        protected Texture GetNodeIcon(GitLock node)
        {
            Texture nodeIcon = null;

            if (!string.IsNullOrEmpty(node.Path))
            {
                nodeIcon = UnityEditorInternal.InternalEditorUtility.GetIconForFile(node.Path);
            }

            if (nodeIcon != null)
            {
                nodeIcon.hideFlags = HideFlags.HideAndDontSave;
            }

            return nodeIcon;
        }

        protected bool LocksControlHasFocus
        {
            get { return GUIUtility.keyboardControl == controlId; }
        }

        private bool SelectNext(int index)
        {
            index++;

            if (index < gitLockEntries.Count)
            {
                SelectedEntry = gitLockEntries[index];
                return true;
            }

            return false;
        }

        private bool SelectPrevious(int index)
        {
            index--;

            if (index >= 0)
            {
                SelectedEntry = gitLockEntries[index];
                return true;
            }

            return false;
        }

        public void ScrollTo(int index, float offset = 0f)
        {
            scroll.Set(scroll.x, Styles.LocksEntryHeight * index + offset);
        }

        public bool OnSelectionChange()
        {
            if (!LocksControlHasFocus)
            {
                GitLockEntry gitLockEntry = GitLockEntry.Default;
                if (Selection.activeObject != lastActivatedObject)
                {
                    var activeAssetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
                    var activeAssetGuid = AssetDatabase.AssetPathToGUID(activeAssetPath);
                    assets.TryGetValue(activeAssetGuid, out gitLockEntry);
                }
                SelectedEntry = gitLockEntry;
                return true;
            }
            return false;
        }
    }

    [Serializable]
    class LocksView : Subview
    {
        [NonSerialized] private bool isBusy;

        [SerializeField] private bool currentRemoteHasUpdate;
        [SerializeField] private bool currentStatusEntriesHasUpdate;
        [SerializeField] private bool currentLocksHasUpdate;
        [SerializeField] private bool keychainHasUpdate;
        [SerializeField] private LocksControl locksControl;
        [SerializeField] private CacheUpdateEvent lastCurrentRemoteChangedEvent;
        [SerializeField] private CacheUpdateEvent lastLocksChangedEvent;
        [SerializeField] private CacheUpdateEvent lastStatusEntriesChangedEvent;
        [SerializeField] private List<GitLock> lockedFiles = new List<GitLock>();
        [SerializeField] private List<GitStatusEntry> gitStatusEntries = new List<GitStatusEntry>();
        [SerializeField] private string currentUsername;
        [SerializeField] private GUIContent unlockFileMenuContent = new GUIContent(Localization.UnlockFileMenuItem);
        [SerializeField] private GUIContent forceUnlockFileMenuContent = new GUIContent(Localization.ForceUnlockFileMenuItem);

        public override void OnEnable()
        {
            base.OnEnable();

            AttachHandlers(Repository);
            ProjectWindowInterface.OptimisticChanged += OnOptimisticChanged;
            ValidateCachedData(Repository);
            KeychainConnectionsChanged();
        }

        public override void OnDisable()
        {
            base.OnDisable();
            DetachHandlers(Repository);
            ProjectWindowInterface.OptimisticChanged -= OnOptimisticChanged;
        }

        // A lock/unlock the user just triggered changed the in-flight set: rebuild so the panel shows
        // the "Acquiring…/Releasing…" row right away, before the server round-trip completes.
        private void OnOptimisticChanged()
        {
            currentLocksHasUpdate = true;
            Redraw();
        }

        public override void Refresh()
        {
            base.Refresh();
            Refresh(CacheType.GitStatus);
            Refresh(CacheType.GitLocks);
        }

        public override void OnDataUpdate()
        {
            base.OnDataUpdate();
            MaybeUpdateData();
        }

        public override void OnGUI()
        {
            var rect = GUILayoutUtility.GetLastRect();

            EditorGUI.BeginDisabledGroup(IsBusy);

            if (locksControl != null && !locksControl.IsEmpty)
            {
                var lockControlRect = new Rect(rect.x, rect.y, Position.width, Position.height - rect.height);

                var requiresRepaint = locksControl.Render(lockControlRect,
                    entry => {},
                    entry => {},
                    entry =>
                    {
                        // No menu on in-flight rows ("Acquiring…/Releasing…") - there's no real lock to act on yet.
                        var assetPath = LockAssetPath(entry);
                        if (ProjectWindowInterface.IsPendingLock(assetPath) || ProjectWindowInterface.IsPendingUnlock(assetPath))
                            return;

                        var menu = new GenericMenu();
                        if (entry.Owner.Name == ProjectWindowInterface.CurrentUsername)
                        {
                            menu.AddItem(unlockFileMenuContent, false, UnlockSelectedEntry);
                        }
                        menu.AddItem(forceUnlockFileMenuContent, false, ForceUnlockSelectedEntry);
                        menu.ShowAsContext();
                    });

                if (requiresRepaint)
                    Redraw();
            }
            else
            {
                DoEmptyGUI();
            }

            EditorGUI.EndDisabledGroup();
            DoProgressGUI();
        }

        private void UnlockSelectedEntry()
        {
            ProjectWindowInterface.ReleaseLock(LockAssetPath(locksControl.SelectedEntry.GitLock), false, ReportUnlockError);
        }

        private void ForceUnlockSelectedEntry()
        {
            ProjectWindowInterface.ReleaseLock(LockAssetPath(locksControl.SelectedEntry.GitLock), true, ReportUnlockError);
        }

        // The lock list stores repo-relative paths; the optimistic/unlock path keys off the Unity
        // project-relative asset path, so convert here (same mapping the lock list uses to find assets).
        private string LockAssetPath(GitLock gitLock)
        {
            return gitLock.Path.MakeAbsolute().RelativeTo(Environment.UnityProjectPath.ToSPath()).ToString();
        }

        private void ReportUnlockError(string error)
        {
            if (!string.IsNullOrEmpty(error) && error.Contains("exit status 255"))
                error = "Failed to unlock: no permissions";
            EditorUtility.DisplayDialog(Localization.ReleaseLockActionTitle, error ?? string.Empty, Localization.Ok);
        }

        private void AttachHandlers(IRepository repository)
        {
            if (repository == null)
            {
                return;
            }

            //Platform.Keychain.ConnectionsChanged += KeychainConnectionsChanged;
            repository.CurrentRemoteChanged += RepositoryOnCurrentRemoteChanged;
            repository.LocksChanged += RepositoryOnLocksChanged;
            repository.StatusEntriesChanged += RepositoryOnStatusEntriesChanged;
        }

        private void DetachHandlers(IRepository repository)
        {
            if (repository == null)
            {
                return;
            }

            //Platform.Keychain.ConnectionsChanged -= KeychainConnectionsChanged;
            repository.CurrentRemoteChanged -= RepositoryOnCurrentRemoteChanged;
            repository.LocksChanged -= RepositoryOnLocksChanged;
            repository.StatusEntriesChanged -= RepositoryOnStatusEntriesChanged;
        }

        private void RepositoryOnCurrentRemoteChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastCurrentRemoteChangedEvent.Equals(cacheUpdateEvent))
            {
                lastCurrentRemoteChangedEvent = cacheUpdateEvent;
                currentRemoteHasUpdate = true;
                Redraw();
            }
        }

        private void RepositoryOnLocksChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastLocksChangedEvent.Equals(cacheUpdateEvent))
            {
                lastLocksChangedEvent = cacheUpdateEvent;
                currentLocksHasUpdate = true;
                Redraw();
            }
        }

        private void RepositoryOnStatusEntriesChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastStatusEntriesChangedEvent.Equals(cacheUpdateEvent))
            {
                lastStatusEntriesChangedEvent = cacheUpdateEvent;
                currentStatusEntriesHasUpdate = true;
                Redraw();
            }
        }

        private void KeychainConnectionsChanged()
        {
            keychainHasUpdate = true;
            Redraw();
        }

        private void ValidateCachedData(IRepository repository)
        {
            repository.CheckAndRaiseEventsIfCacheNewer(CacheType.RepositoryInfo, lastCurrentRemoteChangedEvent);
            repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitLocks, lastLocksChangedEvent);
            repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitStatus, lastStatusEntriesChangedEvent);
        }

        private void MaybeUpdateData()
        {
            if (FirstRender)
            {
                if (locksControl != null)
                {
                    locksControl.LoadIcons();
                }
            }



            if (Repository == null)
            {
                return;
            }

            //if (keychainHasUpdate || currentRemoteHasUpdate)
            //{
            //    var username = String.Empty;
            //    if (Repository != null)
            //    {
            //        Connection connection;
            //        if (!string.IsNullOrEmpty(Repository.CloneUrl))
            //        {
            //            var host = Repository.CloneUrl
            //                                 .ToRepositoryUri()
            //                                 .GetComponents(UriComponents.Host, UriFormat.SafeUnescaped);

            //            connection = Platform.Keychain.Connections.FirstOrDefault(x => x.Host == host);
            //        }
            //        else
            //        {
            //            connection = Platform.Keychain.Connections.FirstOrDefault(HostAddress.IsGitHubDotCom);
            //        }

            //        if (connection != null)
            //        {
            //            username = connection.Username;
            //        }
            //    }

            //    currentUsername = username;

            //    keychainHasUpdate = false;
            //    currentRemoteHasUpdate = false;
            //}

            if (currentLocksHasUpdate)
            {
                lockedFiles = Repository.CurrentLocks;
            }

            if (currentStatusEntriesHasUpdate)
            {
                gitStatusEntries = Repository.CurrentChanges.Where(x => x.Status != GitFileStatus.Ignored).ToList();
            }

            if (currentStatusEntriesHasUpdate || currentLocksHasUpdate)
            {
                currentStatusEntriesHasUpdate = false;
                currentLocksHasUpdate = false;
                BuildLocksControl();
            }
        }

        private void BuildLocksControl()
        {
            if (locksControl == null)
            {
                locksControl = new LocksControl();
            }

            locksControl.projectPath = Environment.UnityProjectPath.ToSPath();
            locksControl.Load(MergePendingLocks(lockedFiles), gitStatusEntries);
        }

        // Add synthetic entries for locks the user just asked for but the server hasn't returned yet, so
        // the panel shows them immediately as "Acquiring…". (Pending unlocks are flagged on existing rows.)
        private List<GitLock> MergePendingLocks(List<GitLock> serverLocks)
        {
            var merged = new List<GitLock>(serverLocks);
            var have = new HashSet<string>(serverLocks.Select(l => l.Path.ToString()));
            var me = new GitUser(ProjectWindowInterface.CurrentUsername ?? string.Empty, string.Empty);
            foreach (var assetPath in ProjectWindowInterface.PendingLockAssetPaths())
            {
                var repoPath = assetPath.ToSPath().RelativeToRepository(Environment);
                if (have.Add(repoPath.ToString()))
                    merged.Add(new GitLock("pending:" + repoPath, repoPath, me, DateTimeOffset.Now));
            }
            return merged;
        }
        public override void OnSelectionChange()
        {
            base.OnSelectionChange();
            if (locksControl.OnSelectionChange())
            {
                Redraw();
            }
        }

        public override bool IsBusy
        {
            get { return isBusy || base.IsBusy; }
        }
    }
}
