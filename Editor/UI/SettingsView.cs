using System;
using Unity.Editor.Tasks;
using Unity.Editor.Tasks.Logging;
using Unity.VersionControl.Git.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Unity.VersionControl.Git.UI
{
    [Serializable]
    class SettingsView : Subview
    {
        private const string GitRepositoryTitle = "Repository Configuration";

        private const string GitRepositoryRemoteLabel = "Remote";
        private const string GitRepositorySave = "Save Repository";
        private const string InitializeGitAttributesLabel = "Setup .gitattributes";
        private const string SetupUnityMergeLabel = "Setup Unity Yaml Merge";

        private const string GeneralSettingsTitle = "General";

        private const string WebTimeoutLabel = "Timeout of web requests";
        private const string GitTimeoutLabel = "Timeout of git commands";

        private const string AutoLockSettingsTitle = "Automatic File Locking";
        private const string AutoLockEnabledLabel = "Enable automatic locking";
        private const string AutoLockOnSaveLabel = "Lock on save";
        private const string AutoLockOnOpenLabel = "Lock on open";
        private const string AutoLockReleaseLabel = "Release my locks when closing the editor";
        private const string AutoLockExtensionsLabel = "File types to lock";
        private const string AutoLockExtensionsHelp = "Extensions locked automatically (space or comma separated). Files marked lockable in .gitattributes are always included.";
        private const string AutoLockExtensionsResetLabel = "Reset to defaults";
        private static readonly string[] AutoLockOnOpenOptions = { "Off", "Automatic", "Ask each time" };
        private static GUIStyle wrappedTextAreaStyle;
        private static GUIStyle WrappedTextAreaStyle => wrappedTextAreaStyle ?? (wrappedTextAreaStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true });

        private const string SyncSettingsTitle = "Sync";
        private const string AutoFetchEnabledLabel = "Fetch from the server automatically";
        private const string AutoFetchIntervalLabel = "Fetch interval (minutes)";
        private const string BlockOutdatedEditLabel = "Block editing outdated files";
        private const string BlockOutdatedCommitLabel = "Block committing outdated files";

        private const string DebugSettingsTitle = "Debug";
        private const string EnableTraceLoggingLabel = "Enable Trace Logging";

        private const string UISceneHierarchySettingsTitle = "UI - Scene Hierarchy";
        private const string UIProjectViewSettingsTitle = "UI - Project Window";
        private const string IconsEnabledToggleLabel = "Show Git Status Icons";
        private const string HierarchyIconsIndentToggleLabel = "Align to end of label";
        private const string HierarchyIconsIndentToggleTooltip = "You probably don't want this";
        private static GUIContent hierarchyIconsIndentToggleContent;
        private static GUIContent HierarchyIconsIndentToggleContent => hierarchyIconsIndentToggleContent ?? (hierarchyIconsIndentToggleContent = new GUIContent(HierarchyIconsIndentToggleLabel, HierarchyIconsIndentToggleTooltip));

        private const string HierarchyIconsOffsetLabel = "Offset";
        private const string HierarchyIconsOffsetRightTooltip = "Offset from the right edge of the hierarchy window. Increase this value to move icons away from the edge.";
        private const string HierarchyIconsOffsetLeftTooltip = "Offset from the left edge of the hierarchy window.";
        private static GUIContent hierarchyIconsOffsetRightContent;
        private static GUIContent HierarchyIconsOffsetRightContent => hierarchyIconsOffsetRightContent ?? (hierarchyIconsOffsetRightContent = new GUIContent(HierarchyIconsOffsetLabel, HierarchyIconsOffsetRightTooltip));
        private static GUIContent hierarchyIconsOffsetLeftContent;
        private static GUIContent HierarchyIconsOffsetLeftContent => hierarchyIconsOffsetLeftContent ?? (hierarchyIconsOffsetLeftContent = new GUIContent(HierarchyIconsOffsetLabel, HierarchyIconsOffsetLeftTooltip));

        private const string HierarchyIconsAlignmentLabel = "Align icons to";
        private const string HierarchyIconsAlignmentTooltip = "Align the icons to the left or right of the hiearchy entry. Note that the icons will visually overlap the scene object buttons when aligned on the left, but the buttons will still work.";
        private static GUIContent hierarchyIconsAlignmentContent;
        private static GUIContent HierarchyIconsAlignmentContent => hierarchyIconsAlignmentContent ?? (hierarchyIconsAlignmentContent = new GUIContent(HierarchyIconsAlignmentLabel, HierarchyIconsAlignmentTooltip));

        private const string DefaultRepositoryRemoteName = "origin";

        [NonSerialized] private bool currentRemoteHasUpdate;
        [NonSerialized] private bool isBusy;

        [SerializeField] private GitPathView gitPathView = new GitPathView();
        [SerializeField] private bool hasRemote;
        [SerializeField] private CacheUpdateEvent lastCurrentRemoteChangedEvent;
        [SerializeField] private string newRepositoryRemoteUrl;
        [SerializeField] private string repositoryRemoteName;
        [SerializeField] private string repositoryRemoteUrl;
        [SerializeField] private Vector2 scroll;
        [SerializeField] private UserSettingsView userSettingsView = new UserSettingsView();

        [SerializeField] private bool repositorySettingsHidden;
        [SerializeField] private bool generalSettingsHidden;
        [SerializeField] private bool autoLockSettingsHidden;
        [SerializeField] private bool syncSettingsHidden;
        [SerializeField] private bool debugSettingsHidden;
        [SerializeField] private bool uiSceneSettingsHidden;
        [SerializeField] private bool uiProjectSettingsHidden;

        public override void InitializeView(IView parent)
        {
            base.InitializeView(parent);
            gitPathView.InitializeView(this);
            userSettingsView.InitializeView(this);
        }

        public override void OnEnable()
        {
            base.OnEnable();
            gitPathView.OnEnable();
            userSettingsView.OnEnable();
            AttachHandlers(Repository);

            if (Repository != null)
            {
                ValidateCachedData(Repository);
            }
        }

        public override void OnDisable()
        {
            base.OnDisable();
            gitPathView.OnDisable();
            userSettingsView.OnDisable();
            DetachHandlers(Repository);
        }

        public override void OnDataUpdate()
        {
            base.OnDataUpdate();
            userSettingsView.OnDataUpdate();
            gitPathView.OnDataUpdate();

            MaybeUpdateData();
        }

        public override void Refresh()
        {
            base.Refresh();
            gitPathView.Refresh();
            userSettingsView.Refresh();
            Refresh(CacheType.RepositoryInfo);
        }

        public override void OnGUI()
        {
            // Widen the label column so longer setting labels aren't clipped by the default (~150px) width.
            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 230;

            scroll = GUILayout.BeginScrollView(scroll);
            {
                userSettingsView.OnGUI();

                GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                if (Repository != null)
                {
                    OnRepositorySettingsGUI();
                    GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                }

                gitPathView.OnGUI();
                OnGeneralSettingsGui();
                OnSyncSettingsGui();
                OnAutoLockSettingsGui();
                OnLoggingSettingsGui();
                OnUISettingsGui();
            }

            GUILayout.EndScrollView();

            EditorGUIUtility.labelWidth = previousLabelWidth;
            DoProgressGUI();
        }

        private void AttachHandlers(IRepository repository)
        {
            if (repository == null)
            {
                return;
            }

            repository.CurrentRemoteChanged += RepositoryOnCurrentRemoteChanged;
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

        private void DetachHandlers(IRepository repository)
        {
            if (repository == null)
            {
                return;
            }

            repository.CurrentRemoteChanged -= RepositoryOnCurrentRemoteChanged;
        }

        private void ValidateCachedData(IRepository repository)
        {
            repository.CheckAndRaiseEventsIfCacheNewer(CacheType.RepositoryInfo, lastCurrentRemoteChangedEvent);
        }

        private void MaybeUpdateData()
        {
            if (Repository == null)
                return;

            if (currentRemoteHasUpdate)
            {
                currentRemoteHasUpdate = false;
                var currentRemote = Repository.CurrentRemote;
                hasRemote = currentRemote.HasValue && !String.IsNullOrEmpty(currentRemote.Value.Url);
                if (!hasRemote)
                {
                    repositoryRemoteName = DefaultRepositoryRemoteName;
                    newRepositoryRemoteUrl = repositoryRemoteUrl = string.Empty;
                }
                else
                {
                    repositoryRemoteName = currentRemote.Value.Name;
                    newRepositoryRemoteUrl = repositoryRemoteUrl = currentRemote.Value.Url;
                }
            }
        }

        private void OnRepositorySettingsGUI()
        {
            repositorySettingsHidden = !Controls.FoldoutScope(!repositorySettingsHidden, GitRepositoryTitle, () =>
            {
                EditorGUI.BeginDisabledGroup(IsBusy);
                {
                    newRepositoryRemoteUrl = EditorGUILayout.TextField(GitRepositoryRemoteLabel + ": " + repositoryRemoteName, newRepositoryRemoteUrl);
                    var needsSaving = newRepositoryRemoteUrl != repositoryRemoteUrl && !String.IsNullOrEmpty(newRepositoryRemoteUrl);

                    EditorGUI.BeginDisabledGroup(!needsSaving);
                    {
                        if (GUILayout.Button(GitRepositorySave, GUILayout.ExpandWidth(false)))
                        {
                            try
                            {
                                isBusy = true;
                                Repository.SetupRemote(repositoryRemoteName, newRepositoryRemoteUrl)
                                    .FinallyInUI((_, __) =>
                                    {
                                        isBusy = false;
                                        Redraw();
                                    })
                                    .Start();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex);
                            }
                        }
                    }
                    EditorGUI.EndDisabledGroup();

                    if (GUILayout.Button(InitializeGitAttributesLabel, GUILayout.ExpandWidth(false)))
                    {
                        bool doit = true;
                        var gitAttrs = Repository.LocalPath.Combine(".gitattributes");
                        if (gitAttrs.FileExists())
                        {
                            doit = EditorUtility.DisplayDialog("Overwrite .gitattributes",
                                "A .gitattributes file already exists. Are you sure you want to overwrite it?",
                                "Overwrite", "Cancel");
                        }

                        if (doit)
                        {
                            try
                            {
                                isBusy = true;
                                SPath unityYamlMergeExec = this.Environment.UnityApplicationContents.ToSPath().Combine("Tools", "UnityYAMLMerge" + Environment.ExecutableExtension);
                                Repository.UpdateGitAttributes()
                                    .Then(Repository.UpdateMergeSettings(unityYamlMergeExec))
                                    .FinallyInUI((_, __) =>
                                    {
                                        isBusy = false;
                                        Redraw();
                                    }).Start();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex);
                            }
                        }
                    }

                    if (GUILayout.Button(SetupUnityMergeLabel, GUILayout.ExpandWidth(false)))
                    {
                        try
                        {
                            isBusy = true;
                            SPath unityYamlMergeExec = this.Environment.UnityApplicationContents.ToSPath().Combine("Tools", "UnityYAMLMerge" + Environment.ExecutableExtension);
                            Repository.UpdateMergeSettings(unityYamlMergeExec).FinallyInUI((_, __) => {
                                isBusy = false;
                                Redraw();
                            }).Start();

                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex);
                        }
                    }
                }
                EditorGUI.EndDisabledGroup();
            });
        }

        private void OnLoggingSettingsGui()
        {
            debugSettingsHidden = !Controls.FoldoutScope(!debugSettingsHidden, DebugSettingsTitle, () =>
            {
                Controls.DoControl(LogHelper.TracingEnabled,
                    value => EditorGUILayout.Toggle(EnableTraceLoggingLabel, value),
                    value =>
                    {
                        LogHelper.TracingEnabled = value;
                        Manager.UserSettings.Set(Constants.TraceLoggingKey, value);
                    });
            });
        }

        private void OnGeneralSettingsGui()
        {
            generalSettingsHidden = !Controls.FoldoutScope(!generalSettingsHidden, GeneralSettingsTitle, () =>
            {
                Controls.DoControl(ApplicationConfiguration.WebTimeout,
                    value => EditorGUILayout.IntField(WebTimeoutLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.WebTimeout = value;
                        Manager.UserSettings.Set(Constants.WebTimeoutKey, value);
                    });

                Controls.DoControl(ApplicationConfiguration.GitTimeout,
                    value => EditorGUILayout.IntField(GitTimeoutLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.GitTimeout = value;
                        Manager.UserSettings.Set(Constants.GitTimeoutKey, value);
                    });
            });
        }

        private void OnSyncSettingsGui()
        {
            syncSettingsHidden = !Controls.FoldoutScope(!syncSettingsHidden, SyncSettingsTitle, () =>
            {
                Controls.DoControl(ApplicationConfiguration.AutoFetchEnabled,
                    value => EditorGUILayout.Toggle(AutoFetchEnabledLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.AutoFetchEnabled = value;
                        Manager.UserSettings.Set(Constants.AutoFetchEnabledKey, value);
                    });

                EditorGUI.BeginDisabledGroup(!ApplicationConfiguration.AutoFetchEnabled);
                Controls.DoControl(ApplicationConfiguration.AutoFetchInterval,
                    value => EditorGUILayout.IntSlider(AutoFetchIntervalLabel, value, 1, 60),
                    value =>
                    {
                        ApplicationConfiguration.AutoFetchInterval = value;
                        Manager.UserSettings.Set(Constants.AutoFetchIntervalKey, value);
                    });
                EditorGUI.EndDisabledGroup();

                Controls.DoControl(ApplicationConfiguration.BlockOutdatedEdit,
                    value => EditorGUILayout.Toggle(BlockOutdatedEditLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.BlockOutdatedEdit = value;
                        Manager.UserSettings.Set(Constants.BlockOutdatedEditKey, value);
                    });

                Controls.DoControl(ApplicationConfiguration.BlockOutdatedCommit,
                    value => EditorGUILayout.Toggle(BlockOutdatedCommitLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.BlockOutdatedCommit = value;
                        Manager.UserSettings.Set(Constants.BlockOutdatedCommitKey, value);
                    });
            });
        }

        private void OnAutoLockSettingsGui()
        {
            autoLockSettingsHidden = !Controls.FoldoutScope(!autoLockSettingsHidden, AutoLockSettingsTitle, () =>
            {
                Controls.DoControl(LfsLocksModificationProcessor.AutoLockEnabled,
                    value => EditorGUILayout.Toggle(AutoLockEnabledLabel, value),
                    value => LfsLocksModificationProcessor.AutoLockEnabled = value);

                EditorGUI.BeginDisabledGroup(!LfsLocksModificationProcessor.AutoLockEnabled);
                {
                    Controls.DoControl(LfsLocksModificationProcessor.LockOnSave,
                        value => EditorGUILayout.Toggle(AutoLockOnSaveLabel, value),
                        value => LfsLocksModificationProcessor.LockOnSave = value);

                    Controls.DoControl(LfsLocksModificationProcessor.OnOpenMode,
                        value => EditorGUILayout.Popup(AutoLockOnOpenLabel, value, AutoLockOnOpenOptions),
                        value => LfsLocksModificationProcessor.OnOpenMode = value);

                    Controls.DoControl(LfsLocksModificationProcessor.ReleaseOnClose,
                        value => EditorGUILayout.Toggle(AutoLockReleaseLabel, value),
                        value => LfsLocksModificationProcessor.ReleaseOnClose = value);

                    GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                    EditorGUILayout.LabelField(AutoLockExtensionsLabel);
                    EditorGUILayout.HelpBox("Lockable files are defined by the 'lockable' rules in your .gitattributes (committed, shared by the team). Edit .gitattributes to change them.", MessageType.None);
                }
                EditorGUI.EndDisabledGroup();
            });
        }

        private void OnUISettingsGui()
        {
            bool dirty = false;

            uiSceneSettingsHidden = !Controls.FoldoutScope(!uiSceneSettingsHidden, UISceneHierarchySettingsTitle, () =>
            {
                Controls.DoControl(ApplicationConfiguration.HierarchyIconsEnabled,
                    value => EditorGUILayout.Toggle(IconsEnabledToggleLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.HierarchyIconsEnabled = value;
                        Manager.UserSettings.Set(Constants.HierarchyIconsEnabledKey, value);
                        dirty = true;
                    });

                Controls.DoControl(ApplicationConfiguration.HierarchyIconsAlignment,
                    value => (ApplicationConfiguration.HierarchyIconAlignment) EditorGUILayout.EnumPopup(HierarchyIconsAlignmentContent, value),
                    value =>
                    {
                        ApplicationConfiguration.HierarchyIconsAlignment = value;
                        Manager.UserSettings.Set(Constants.HierarchyIconsAlignmentKey, value);
                        dirty = true;
                    });

                if (ApplicationConfiguration.HierarchyIconsAlignment == ApplicationConfiguration.HierarchyIconAlignment.Right)
                {
                    Controls.DoControl(ApplicationConfiguration.HierarchyIconsIndented,
                        value => EditorGUILayout.Toggle(HierarchyIconsIndentToggleContent, value),
                        value =>
                        {
                            ApplicationConfiguration.HierarchyIconsIndented = value;
                            Manager.UserSettings.Set(Constants.HierarchyIconsIndentedKey, value);
                            dirty = true;
                        });

                    Controls.DoControl(ApplicationConfiguration.HierarchyIconsOffsetRight,
                        value => EditorGUILayout.IntSlider(HierarchyIconsOffsetRightContent, value, 0, 200),
                        value =>
                        {
                            ApplicationConfiguration.HierarchyIconsOffsetRight = value;
                            Manager.UserSettings.Set(Constants.HierarchyIconsOffsetRightKey, value);
                            dirty = true;
                        });
                }
                else
                {
                    Controls.DoControl(ApplicationConfiguration.HierarchyIconsOffsetLeft,
                        value => EditorGUILayout.IntSlider(HierarchyIconsOffsetLeftContent, value, -16, 16),
                        value =>
                        {
                            ApplicationConfiguration.HierarchyIconsOffsetLeft = value;
                            Manager.UserSettings.Set(Constants.HierarchyIconsOffsetLeftKey, value);
                            dirty = true;
                        });
                }
            });

            if (dirty)
            {
                EditorApplication.RepaintHierarchyWindow();
            }

            dirty = false;

            uiProjectSettingsHidden = !Controls.FoldoutScope(!uiProjectSettingsHidden, UIProjectViewSettingsTitle, () =>
            {
                Controls.DoControl(ApplicationConfiguration.ProjectIconsEnabled,
                    value => EditorGUILayout.Toggle(IconsEnabledToggleLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.ProjectIconsEnabled = value;
                        Manager.UserSettings.Set(Constants.ProjectIconsEnabledKey, value);
                        dirty = true;
                    });

                if (dirty)
                {
                    EditorApplication.RepaintProjectWindow();
                }
            });
        }

        public override bool IsBusy => isBusy || userSettingsView.IsBusy || gitPathView.IsBusy;
    }
}
