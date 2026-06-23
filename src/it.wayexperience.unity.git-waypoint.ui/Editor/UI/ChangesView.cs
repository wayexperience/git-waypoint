using System;
using System.Collections.Generic;
using System.Linq;
using SpoiledCat.Git;
using Unity.Editor.Tasks;
using Unity.VersionControl.Git.Tasks;
using UnityEditor;
using UnityEngine;

namespace Unity.VersionControl.Git.UI
{
    using IO;

    [Serializable]
    class ChangesView : Subview
    {
        private const string SummaryLabel = "Commit summary";
        private const string DescriptionLabel = "Commit description";
        private const string CommitButton = "Commit to <b>{0}</b>";
        private const string CommitAndPushButton = "Commit & Push";
        private const string SelectAllButton = "All";
        private const string SelectNoneButton = "None";
        private const string ChangedFilesLabel = "{0} changed files";
        private const string OneChangedFileLabel = "1 changed file";
        private const string NoChangedFilesLabel = "No changed files";

        [SerializeField] private bool currentBranchHasUpdate;
        [SerializeField] private bool currentStatusEntriesHasUpdate;
        [SerializeField] private bool currentLocksHasUpdate;

        [NonSerialized] private GUIContent discardGuiContent;
        [NonSerialized] private bool isBusy;

        [SerializeField] private string commitBody = "";
        [SerializeField] private string commitMessage = "";
        [SerializeField] private string currentBranch = "[unknown]";

        [SerializeField] private Vector2 treeScroll;
        [SerializeField] private ChangesTree treeChanges = new ChangesTree { DisplayRootNode = false, IsCheckable = true, IsUsingGlobalSelection = true };

        [SerializeField] private HashSet<SPath> gitLocks = new HashSet<SPath>();
        [SerializeField] private HashSet<SPath> gitLocksByMe = new HashSet<SPath>();
        [SerializeField] private List<GitStatusEntry> gitStatusEntries = new List<GitStatusEntry>();

        [SerializeField] private string changedFilesText = NoChangedFilesLabel;

        [SerializeField] private CacheUpdateEvent lastCurrentBranchChangedEvent;
        [SerializeField] private CacheUpdateEvent lastStatusEntriesChangedEvent;
        [SerializeField] private CacheUpdateEvent lastLocksChangedEvent;

        public override void OnEnable()
        {
            base.OnEnable();

            AttachHandlers(Repository);
            ValidateCachedData(Repository);
        }

        public override void OnDisable()
        {
            base.OnDisable();
            DetachHandlers(Repository);
        }

        public override void Refresh()
        {
            base.Refresh();
            Refresh(CacheType.GitStatus);
            Refresh(CacheType.RepositoryInfo);
            Refresh(CacheType.GitLocks);
        }

        public override void OnDataUpdate()
        {
            base.OnDataUpdate();
            MaybeUpdateData();
        }

        public override void OnGUI()
        {
            DoButtonBarGUI();
            if (gitStatusEntries.Count == 0)
            {
                GUILayout.BeginVertical(Styles.CommitFileAreaStyle);
                DoEmptyGUI();
                GUILayout.EndVertical();
            }
            else
            {
                EditorGUI.BeginDisabledGroup(isBusy);
                DoChangesTreeGUI();
                EditorGUI.EndDisabledGroup();
            }
            EditorGUI.BeginDisabledGroup(isBusy);

            DoProgressGUI();

            // Do the commit details area
            DoCommitGUI();
            EditorGUI.EndDisabledGroup();
        }

        public override void OnSelectionChange()
        {
            base.OnSelectionChange();
            if (treeChanges.OnSelectionChange())
            {
                Redraw();
            }
        }

        public override void OnFocusChanged()
        {
            base.OnFocusChanged();
            var hasFocus = HasFocus;
            if (treeChanges.ViewHasFocus != hasFocus)
            {
                treeChanges.ViewHasFocus = hasFocus;
                Redraw();
            }
        }

        private void DoChangesTreeGUI()
        {
            var rect = GUILayoutUtility.GetLastRect();
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(Styles.CommitFileAreaStyle);
            {
                treeScroll = GUILayout.BeginScrollView(treeScroll);
                {
                    OnTreeGUI(new Rect(0f, 0f, Position.width, Position.height - rect.height + Styles.CommitAreaPadding));
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private void DoButtonBarGUI()
        {
            GUILayout.BeginHorizontal();
            {
                EditorGUI.BeginDisabledGroup(gitStatusEntries == null || gitStatusEntries.Count == 0);
                {
                    if (GUILayout.Button(SelectAllButton, EditorStyles.miniButtonLeft))
                    {
                        SelectAll();
                    }

                    if (GUILayout.Button(SelectNoneButton, EditorStyles.miniButtonRight))
                    {
                        SelectNone();
                    }
                }
                EditorGUI.EndDisabledGroup();

                GUILayout.FlexibleSpace();

                GUILayout.Label(changedFilesText, EditorStyles.miniLabel);
            }
            GUILayout.EndHorizontal();
        }

        private void OnTreeGUI(Rect rect)
        {
            if (treeChanges != null)
            {
                treeChanges.FolderStyle = Styles.Foldout;
                treeChanges.TreeNodeStyle = Styles.TreeNode;
                treeChanges.ActiveTreeNodeStyle = Styles.ActiveTreeNode;
                treeChanges.FocusedTreeNodeStyle = Styles.FocusedTreeNode;
                treeChanges.FocusedActiveTreeNodeStyle = Styles.FocusedActiveTreeNode;

                var treeRenderRect = treeChanges.Render(rect, treeScroll,
                    node => { },
                    ShowDiff,
                    node => {
                        var menu = CreateContextMenu(node);
                        menu.ShowAsContext();
                    });

                if (treeChanges.RequiresRepaint)
                    Redraw();

                GUILayout.Space(treeRenderRect.y - rect.y);
            }
        }

        private GenericMenu CreateContextMenu(ChangesTreeNode node)
        {
            var genericMenu = new GenericMenu();

            genericMenu.AddItem(new GUIContent("Show Diff"), false, () => ShowDiff(node));

            genericMenu.AddSeparator("");

            if (discardGuiContent == null)
            {
                discardGuiContent = new GUIContent("Discard");
            }

            genericMenu.AddItem(discardGuiContent, false, () =>
            {
                if (!EditorUtility.DisplayDialog(Localization.DiscardConfirmTitle,
                                Localization.DiscardConfirmDescription,
                                Localization.DiscardConfirmYes,
                                Localization.Cancel))
                    return;

                GitStatusEntry[] discardEntries;
                if (node.isFolder)
                {
                    discardEntries = treeChanges
                        .GetLeafNodes(node)
                        .Select(treeNode => treeNode.GitStatusEntry)
                        .ToArray();
                }
                else
                {
                    discardEntries = new [] { node.GitStatusEntry };
                }

                // FinallyInUI runs whether or not the task reports success (the discard process
                // can complete without the chain flagging success), so refresh/unlock always happen.
                Repository.DiscardChanges(discardEntries)
                          .FinallyInUI((success, ex) =>
                          {
                              AssetDatabase.Refresh();

                              // Discarding abandons your edits, so release your own locks on those files.
                              var me = ProjectWindowInterface.CurrentUsername;
                              var currentLocks = Repository.CurrentLocks;
                              if (currentLocks != null && !string.IsNullOrEmpty(me))
                              {
                                  foreach (var entry in discardEntries)
                                  {
                                      var p = entry.Path.ToSPath();
                                      foreach (var lck in currentLocks)
                                      {
                                          if (lck.Path == p && lck.Owner.Name == me)
                                          {
                                              Repository.ReleaseLock(p, false).Start();
                                              break;
                                          }
                                      }
                                  }
                              }

                              // The native watcher is unavailable on arm64, so refresh status/locks explicitly.
                              Repository.Refresh(CacheType.GitStatus);
                              Repository.Refresh(CacheType.GitLocks);
                          })
                          .Start();
            });

            return genericMenu;
        }

        private void ShowDiff(ChangesTreeNode node)
        {
            ITask<SPath[]> calculateDiff = null;
            if (node.IsFolder)
            {
                calculateDiff = CalculateFolderDiff(node);
            }
            else
            {
                calculateDiff = CalculateFileDiff(node);
            }

            calculateDiff.FinallyInUI((s, ex, leftRight) => {
                if (!s || leftRight == null)
                {
                    UnityEngine.Debug.LogWarning("Show Diff: couldn't prepare the diff: " + (ex != null ? ex.Message : "unknown"));
                    return;
                }
                // Try a diff tool we can find ourselves first, so this works without the user configuring
                // Unity's external diff tool (which is what was leaving "Show Diff" doing nothing).
                if (TryOpenDiffExternally(leftRight[0], leftRight[1]))
                    return;
                EditorUtility.InvokeDiffTool(leftRight[0].IsInitialized ? leftRight[0].FileName : null,
                    leftRight[0].IsInitialized ? leftRight[0].MakeAbsolute().ToString() : null,
                    leftRight[1].IsInitialized ? leftRight[1].FileName : null,
                    leftRight[1].IsInitialized ? leftRight[1].MakeAbsolute().ToString() : null, null, null);
            }).Start();
        }

        // Open the two versions side by side in a diff tool we locate directly (macOS FileMerge, else
        // VS Code), so Show Diff works without configuring Unity's external diff tool. Returns false to
        // fall back to Unity's configured tool.
        private static bool TryOpenDiffExternally(SPath left, SPath right)
        {
            if (!left.IsInitialized || !right.IsInitialized)
                return false;
            var l = left.MakeAbsolute().ToString();
            var r = right.MakeAbsolute().ToString();

            if (Application.platform == RuntimePlatform.OSXEditor && System.IO.File.Exists("/usr/bin/opendiff")
                && TryStartDiff("/usr/bin/opendiff", "\"" + l + "\" \"" + r + "\""))
                return true;

            foreach (var code in new[] { "/opt/homebrew/bin/code", "/usr/local/bin/code" })
                if (System.IO.File.Exists(code) && TryStartDiff(code, "--diff \"" + l + "\" \"" + r + "\""))
                    return true;

            return false;
        }

        private static bool TryStartDiff(string exe, string args)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                });
                return true;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogWarning("Show Diff: failed to launch " + exe + ": " + e.Message);
                return false;
            }
        }

        private ITask<SPath[]> CalculateFolderDiff(ChangesTreeNode node)
        {
            var rightFile = node.Path.ToSPath();
            var tmpDir = Manager.Environment.UnityProjectPath.ToSPath().Combine("Temp").CreateTempDirectory();
            var changedFiles = treeChanges.GetLeafNodes(node).Select(x => x.Path.ToSPath()).ToList();

            return TaskManager.With(files => {
                var leftFolder = tmpDir.Combine("left", rightFile.FileName);
                var rightFolder = tmpDir.Combine("right", rightFile.FileName);
                foreach (var file in files)
                {
                    var txt = new GitProcessTask(Platform,
                            "show HEAD:\"" + file.ToString(SlashMode.Forward) + "\"")
                              .Configure(Manager.ProcessManager)
                              .Catch(_ => true)
                              .RunSynchronously();

                    if (txt != null)
                        leftFolder.Combine(file.RelativeTo(rightFile))
                                  .WriteAllText(txt);
                    if (file.FileExists())
                        rightFolder.Combine(file.RelativeTo(rightFile))
                                   .WriteAllText(file.ReadAllText());
                }
                return new SPath[] { leftFolder, rightFolder };
            }, changedFiles, "Calculating diff...");
        }

        private ITask<SPath[]> CalculateFileDiff(ChangesTreeNode node)
        {
            var rightFile = node.Path.ToSPath();
            var tmpDir = Manager.Environment.UnityProjectPath.ToSPath().Combine("Temp", "ghu-diffs").EnsureDirectoryExists();
            var leftFile = tmpDir.Combine(rightFile.FileNameWithoutExtension + "_" + Repository.CurrentHead + rightFile.ExtensionWithDot);
            return new GitProcessTask(Platform, "show HEAD:\"" + rightFile.ToString(SlashMode.Forward) + "\"")
                   .Configure(Manager.ProcessManager)
                   .Catch(_ => true)
                   .Then((success, ex, txt) =>
                   {
                       // both files exist, just compare them
                       if (success && rightFile.FileExists())
                       {
                           leftFile.WriteAllText(txt);
                           return new SPath[] { leftFile, rightFile };
                       }

                       var leftFolder = tmpDir.Combine("left", leftFile.FileName).EnsureDirectoryExists();
                       var rightFolder = tmpDir.Combine("right", leftFile.FileName).EnsureDirectoryExists();
                       // file was deleted
                       if (!rightFile.FileExists())
                       {
                           leftFolder.Combine(rightFile).WriteAllText(txt);
                       }

                       // file was created
                       if (!success)
                       {
                           rightFolder.Combine(rightFile).WriteAllText(rightFile.ReadAllText());
                       }
                       return new SPath[] { leftFolder.Combine(rightFile), rightFolder.Combine(rightFile) };
                   });
        }

        private void RepositoryOnStatusEntriesChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastStatusEntriesChangedEvent.Equals(cacheUpdateEvent))
            {
                ReceivedEvent(cacheUpdateEvent.cacheType);
                lastStatusEntriesChangedEvent = cacheUpdateEvent;
                currentStatusEntriesHasUpdate = true;
                Redraw();
            }
        }

        private void RepositoryOnCurrentBranchChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastCurrentBranchChangedEvent.Equals(cacheUpdateEvent))
            {
                ReceivedEvent(cacheUpdateEvent.cacheType);
                lastCurrentBranchChangedEvent = cacheUpdateEvent;
                currentBranchHasUpdate = true;
                Redraw();
            }
        }

        private void RepositoryOnLocksChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastLocksChangedEvent.Equals(cacheUpdateEvent))
            {
                ReceivedEvent(cacheUpdateEvent.cacheType);
                lastLocksChangedEvent = cacheUpdateEvent;
                currentLocksHasUpdate = true;
                Redraw();
            }
        }

        private void AttachHandlers(IRepository repository)
        {
            if (repository == null)
            {
                return;
            }

            repository.CurrentBranchChanged += RepositoryOnCurrentBranchChanged;
            repository.StatusEntriesChanged += RepositoryOnStatusEntriesChanged;
            repository.LocksChanged += RepositoryOnLocksChanged;
        }

        private void DetachHandlers(IRepository repository)
        {
            if (repository == null)
            {
                return;
            }

            repository.CurrentBranchChanged -= RepositoryOnCurrentBranchChanged;
            repository.StatusEntriesChanged -= RepositoryOnStatusEntriesChanged;
            repository.LocksChanged -= RepositoryOnLocksChanged;
        }

        private void ValidateCachedData(IRepository repository)
        {
            repository.CheckAndRaiseEventsIfCacheNewer(CacheType.RepositoryInfo, lastCurrentBranchChangedEvent);
            repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitStatus, lastStatusEntriesChangedEvent);
            repository.CheckAndRaiseEventsIfCacheNewer(CacheType.GitLocks, lastLocksChangedEvent);
        }

        private void MaybeUpdateData()
        {
            if (FirstRender && treeChanges != null)
            {
                treeChanges.ViewHasFocus = HasFocus;
            }

            treeChanges?.UpdateIcons(Styles.FolderIcon);

            if (currentBranchHasUpdate)
            {
                currentBranchHasUpdate = false;
                currentBranch = Repository.CurrentBranchName;
            }

            if (currentLocksHasUpdate)
            {
                gitLocks = new HashSet<SPath>(Repository.CurrentLocks.Select(gitLock => gitLock.Path));
                var me = ProjectWindowInterface.CurrentUsername;
                gitLocksByMe = string.IsNullOrEmpty(me)
                    ? new HashSet<SPath>()
                    : new HashSet<SPath>(Repository.CurrentLocks.Where(l => l.Owner.Name == me).Select(l => l.Path));
            }

            if (currentStatusEntriesHasUpdate)
            {
                gitStatusEntries = Repository.CurrentChanges.Where(x => x.Status != GitFileStatus.Ignored).ToList();

                changedFilesText = gitStatusEntries.Count == 0
                    ? NoChangedFilesLabel
                    : gitStatusEntries.Count == 1
                        ? OneChangedFileLabel
                        : String.Format(ChangedFilesLabel, gitStatusEntries.Count);
            }

            if (currentStatusEntriesHasUpdate || currentLocksHasUpdate)
            {
                currentStatusEntriesHasUpdate = false;
                currentLocksHasUpdate = false;

                BuildTree();
            }
        }

        private void BuildTree()
        {
            treeChanges.PathSeparator = SPath.FileSystem.DirectorySeparatorChar.ToString();
            treeChanges.Load(gitStatusEntries.Select(entry =>
            {
                var sp = entry.Path.ToSPath();
                return new GitStatusEntryTreeData(entry, gitLocks.Contains(sp), gitLocksByMe.Contains(sp));
            }));
            Redraw();
        }

        private void DoCommitGUI()
        {
            GUILayout.BeginHorizontal();
            {
                GUILayout.Space(Styles.CommitAreaPadding);

                GUILayout.BeginVertical(GUILayout.Height(
                        Mathf.Clamp(Position.height * Styles.CommitAreaDefaultRatio,
                        Styles.CommitAreaMinHeight,
                        Styles.CommitAreaMaxHeight))
                );
                {
                    GUILayout.Space(Styles.CommitAreaPadding);

                    GUILayout.Label(SummaryLabel);
                    commitMessage = EditorGUILayout.TextField(commitMessage, Styles.TextFieldStyle);

                    GUILayout.Space(Styles.CommitAreaPadding * 2);

                    GUILayout.Label(DescriptionLabel);
                    commitBody = EditorGUILayout.TextArea(commitBody, Styles.CommitDescriptionFieldStyle, GUILayout.ExpandHeight(true));

                    GUILayout.Space(Styles.CommitAreaPadding);

                    // Disable committing when already committing or if we don't have all the data needed
                    //Debug.LogFormat("IsBusy:{0} string.IsNullOrEmpty(commitMessage): {1} treeChanges.GetCheckedFiles().Any(): {2}",
                    //    IsBusy, string.IsNullOrEmpty(commitMessage), treeChanges.GetCheckedFiles().Any());
                    // Optionally stop you committing a file that's outdated (a newer version is on the
                    // server) until you've pulled - committing on top of it would just make a conflict.
                    bool outdatedBlocked = ApplicationConfiguration.BlockOutdatedCommit && HasOutdatedCheckedFiles();
                    if (outdatedBlocked)
                        EditorGUILayout.HelpBox("Some selected files are outdated. Pull the latest version before committing.", MessageType.Warning);

                    EditorGUI.BeginDisabledGroup(IsBusy || string.IsNullOrEmpty(commitMessage) || !treeChanges.GetCheckedFiles().Any() || outdatedBlocked);
                    {
                        GUILayout.BeginHorizontal();
                        {
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(String.Format(CommitButton, currentBranch), Styles.CommitButtonStyle))
                            {
                                GUI.FocusControl(null);
                                Commit();
                            }
                            // Commit & Push (Anchorpoint-style "sync"): commit, then send it straight away.
                            // Only when there's a remote to push to.
                            if (Repository != null && Repository.CurrentRemote.HasValue
                                && GUILayout.Button(CommitAndPushButton, Styles.CommitButtonStyle))
                            {
                                GUI.FocusControl(null);
                                Commit(true);
                            }
                        }
                        GUILayout.EndHorizontal();
                    }
                    EditorGUI.EndDisabledGroup();

                    GUILayout.Space(Styles.CommitAreaPadding);
                }
                GUILayout.EndVertical();

                GUILayout.Space(Styles.CommitAreaPadding);
            }
            GUILayout.EndHorizontal();
        }

        private void SelectAll()
        {
            this.treeChanges.SetCheckStateOnAll(true);
        }

        private void SelectNone()
        {
            this.treeChanges.SetCheckStateOnAll(false);
        }

        private bool HasOutdatedCheckedFiles()
        {
            foreach (var f in treeChanges.GetCheckedFiles())
            {
                var guid = AssetDatabase.AssetPathToGUID(f);
                if (!string.IsNullOrEmpty(guid) && ProjectWindowInterface.IsOutdated(guid))
                    return true;
            }
            return false;
        }

        private void Commit(bool push = false)
        {
            isBusy = true;
            var files = treeChanges.GetCheckedFiles().ToList();
            ITask addTask = null;

            if (files.Count == gitStatusEntries.Count)
            {
                addTask = Repository.CommitAllFiles(commitMessage, commitBody);
            }
            else
            {
                ITask commit = Repository.CommitFiles(files, commitMessage, commitBody);

                // if there are files that have been staged outside of Unity, but they aren't selected for commit, remove them
                // from the index before commiting, otherwise the commit will take them along.
                var filesStagedButNotChecked = gitStatusEntries.Where(x => x.Staged).Select(x => x.Path).Except(files).ToList();
                if (filesStagedButNotChecked.Count > 0)
                    addTask = GitClient.Remove(filesStagedButNotChecked);
                addTask = addTask == null ? commit : addTask.Then(commit);
            }

            addTask
                .FinallyInUI((success, exception) =>
                    {
                        if (success)
                        {
                            //UsageTracker.IncrementChangesViewButtonCommit();

                            commitMessage = "";
                            commitBody = "";

                            // The native watcher is unavailable on arm64, so the just-committed files
                            // would otherwise linger in the list until the next refresh. Refresh now.
                            Repository.Refresh(CacheType.GitStatus);
                            Repository.Refresh(CacheType.GitLocks);

                            if (push)
                            {
                                PushAfterCommit();
                                return; // keep busy until the push finishes
                            }
                        }
                        isBusy = false;
                    }).Start();
        }

        // Second half of "Commit & Push": send the commit we just made, then drop locks on the files
        // that are now in sync (same as a normal push).
        private void PushAfterCommit()
        {
            Repository
                .Push()
                .FinallyInUI((success, exception) =>
                {
                    if (success)
                        LfsLocksModificationProcessor.ReleaseLocksAfterPush();
                    else
                        EditorUtility.DisplayDialog("Push", exception.Message, Localization.Ok);
                    isBusy = false;
                })
                .Start();
        }

        public override bool IsBusy
        {
            get { return isBusy || base.IsBusy; }
        }
    }
}
