using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Editor.Tasks;
using Unity.Editor.Tasks.Logging;
using Unity.VersionControl.Git.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    using IO;

    // A modern, UI-Toolkit window for Git for Unity, built to the design mockups. Changes and Locks are native.
    // History/Branches/Settings show a placeholder that opens the classic IMGUI window for now (those IMGUI
    // views are too coupled to their host to embed cleanly; they'll be rebuilt natively next). The classic
    // window stays available and untouched.
    class GitWaypointWindow : EditorWindow
    {
        enum Tab { Changes, Locks, History, Branches, Settings }
        enum LockFilter { All, Mine, Team }
        enum Sev { Info, Ok, Warn, Error }

        static readonly Color TabInactive = new Color(0.70f, 0.70f, 0.74f); // readable inactive tab label

        // Notifications: a persistent bottom status strip (every action, discreet) + floating toasts
        // (errors and important outcomes). The top pill shows only branch sync state / "Syncing".
        VisualElement statusStrip, toastContainer;
        Label statusIcon, statusMessage, statusTime;
        double statusStamp = -1;

        // Header / toolbar
        Label branchLabel, remoteLabel, syncLabel, dotsLabel;
        Button fetchButton, pullButton, pushButton;
        string runningOp;
        double opStartTime;

        // Tabs
        Tab activeTab = Tab.Changes;
        readonly Dictionary<Tab, Button> tabButtons = new Dictionary<Tab, Button>();
        readonly Dictionary<Tab, VisualElement> tabContents = new Dictionary<Tab, VisualElement>();

        // Changes
        ScrollView changesList;
        Label emptyLabel, changesCount;
        TextField searchField, commitMessage, commitDescription;
        Button commitButton, commitPushButton, discardAllButton;
        Toggle selectAllToggle;
        VisualElement changesSelRow;
        VisualElement outdatedBanner;
        Label outdatedBannerLabel;
        readonly HashSet<string> checkedPaths = new HashSet<string>();
        readonly HashSet<string> knownPaths = new HashSet<string>();
        string searchText = "";
        enum ChangesFilter { All, Modified, Added, Deleted, Outdated, Locked }
        ChangesFilter changesFilter = ChangesFilter.All;
        Button changesFilterButton;

        // Locks
        VisualElement lockStatsRow;
        ScrollView locksList;
        Label locksEmpty, lockAutoLabel;
        TextField lockSearchField;
        string lockSearchText = "";
        LockFilter lockFilter = LockFilter.All;
        readonly Dictionary<LockFilter, Button> lockFilterButtons = new Dictionary<LockFilter, Button>();

        // History
        ScrollView historyList;
        VisualElement historyDetail;
        Label historyEmpty, historyHeader;
        string selectedCommit;
        // Commits on the upstream not yet pulled (git log HEAD..@{u}), shown above local history so you can
        // see how far behind you are and what's coming. Cached and rendered synchronously to avoid flicker.
        [NonSerialized] List<GitLogEntry> incomingLog = new List<GitLogEntry>();
        [NonSerialized] bool incomingInFlight;

        // Branches
        ScrollView branchesList;
        Label branchesEmpty;
        TextField newBranchField;

        bool subscribed, needsRebuild, isBusy;
        bool historyRequested, branchesRequested;

        // Ahead/behind per local tracking branch, cached so branch rows render their chip synchronously.
        // Rebuilding the list re-reads this dict (no flicker); the values are refreshed out-of-band and only
        // trigger a rebuild when they actually change. The in-flight set keeps repeated rebuilds from re-querying.
        readonly Dictionary<string, GitAheadBehindStatus> aheadBehindByBranch = new Dictionary<string, GitAheadBehindStatus>();
        readonly HashSet<string> aheadBehindInFlight = new HashSet<string>();

        // Identity onboarding: banner shown when git name/email aren't set yet.
        VisualElement identityBanner;
        bool userSubscribed;
        bool identitySavePending;

        // Settings-tab identity fields + validator, refreshed when the GitUser cache lands asynchronously.
        TextField settingsNameField, settingsEmailField;
        Action settingsValidateUser;

        // Git installation settings (kept as fields so they refresh after Find/Apply/bundled actions).
        TextField gitPathField, lfsPathField;
        Label gitVersionLabel, lfsVersionLabel;
        CacheUpdateEvent lastUserEvent;

        const int StaleHours = 24;

        [MenuItem("Window/Git Waypoint")]
        public static void Open()
        {
            var window = GetWindow<GitWaypointWindow>();
            window.ApplyTitle();
            window.minSize = new Vector2(360, 420);
            window.Show();
        }

        // The tab icon's Texture2D is HideAndDontSave, so it's lost on every domain reload; re-apply it from
        // CreateGUI (which runs after each reload) as well as on open, or the icon silently disappears.
        void ApplyTitle()
        {
            // Light logo on the dark editor theme, dark logo on the light theme, so the tab icon stays visible.
            var logo = EditorGUIUtility.isProSkin ? "small-logo-light" : "small-logo";
            titleContent = new GUIContent("Git Waypoint", Utility.GetIcon(logo, logo + "@2x"));
        }

        IApplicationManager Manager => EntryPoint.ApplicationManager;
        IGitEnvironment Env => Manager != null ? Manager.Environment : null;
        IRepository Repository => Env != null ? Env.Repository : null;

        void CreateGUI()
        {
            // Any operation in flight before a domain reload is gone now; clear stale op state so the
            // safety-net timeout doesn't fire immediately (opStartTime would otherwise be 0 = "ages ago").
            runningOp = null;
            isBusy = false;
            opStartTime = EditorApplication.timeSinceStartup;

            ApplyTitle();
            BuildUI();
            Subscribe();
            SubscribeUser();
            // Opening (or reloading) the window asks for a prompt fetch so incoming commits / behind counts
            // show up without pressing Fetch. Throttled inside GitAutoFetch; respects the auto-fetch setting.
            GitAutoFetch.RequestFetchSoon();
            ProjectWindowInterface.OptimisticChanged += OnOptimisticChanged;
            SetActiveTab(activeTab);
            needsRebuild = true;
            rootVisualElement.schedule.Execute(() =>
            {
                if (!subscribed && Repository != null) { Subscribe(); needsRebuild = true; }
                if (!userSubscribed) SubscribeUser();
                if (needsRebuild) { needsRebuild = false; RefreshActive(); }

                // Last-resort safety net for an op whose completion callback never fires at all. A genuinely
                // blocked op (e.g. a locked SSH agent) already fails fast (~10s) via fail-fast SSH and reports
                // a real error, so this only needs to catch a true hang - keep it generous so it doesn't fire
                // on a legitimately slow first fetch / a queued op.
                if (runningOp != null && EditorApplication.timeSinceStartup - opStartTime > 30)
                {
                    var op = runningOp;
                    runningOp = null;
                    needsRebuild = true;
                    SetStatus(op + " is taking too long", Sev.Warn);
                    Toast(op + " timed out", "It didn't finish in time. Check the Console or your connection.", Sev.Warn);
                    Debug.LogWarning("Git Waypoint: " + op + " didn't return within 30s.");
                }

                // Keep the status strip's relative time fresh.
                if (statusStamp >= 0 && statusTime != null)
                    statusTime.text = AgoShort(EditorApplication.timeSinceStartup - statusStamp);
            }).Every(250);

            // Animate the running-op dots in a fixed-width label, so the status text never jitters.
            rootVisualElement.schedule.Execute(() =>
            {
                if (runningOp == null) { if (dotsLabel.text.Length > 0) dotsLabel.text = ""; return; }
                int dots = 1 + (int)(EditorApplication.timeSinceStartup * 3) % 3;
                dotsLabel.text = new string('.', dots);
            }).Every(150);
        }

        void OnDisable()
        {
            Unsubscribe();
            UnsubscribeUser();
            ProjectWindowInterface.OptimisticChanged -= OnOptimisticChanged;
        }

        void OnOptimisticChanged() => needsRebuild = true;

        // ---- Identity onboarding -------------------------------------------------------------------

        // Artists often open Unity with no git name/email configured. Show a banner above every tab that
        // points to Settings (the one place identity is edited) — no duplicated fields here.
        void BuildIdentityBanner(VisualElement root)
        {
            identityBanner = new VisualElement();
            identityBanner.style.display = DisplayStyle.None;
            identityBanner.style.flexDirection = FlexDirection.Row;
            identityBanner.style.alignItems = Align.Center;
            identityBanner.style.marginLeft = 10; identityBanner.style.marginRight = 10;
            identityBanner.style.marginTop = 8; identityBanner.style.marginBottom = 2;
            identityBanner.style.paddingTop = 8; identityBanner.style.paddingBottom = 8;
            identityBanner.style.paddingLeft = 10; identityBanner.style.paddingRight = 10;
            identityBanner.style.backgroundColor = new Color(GitWaypointTheme.Accent.r, GitWaypointTheme.Accent.g, GitWaypointTheme.Accent.b, 0.12f);
            GitWaypointTheme.Round(identityBanner, 4);

            var col = new VisualElement { style = { flexGrow = 1, flexShrink = 1, marginRight = 10 } };
            col.Add(new Label("Set your Git identity")
                { style = { unityFontStyleAndWeight = FontStyle.Bold, color = GitWaypointTheme.Text, fontSize = 12 } });
            col.Add(new Label("Add your name and email in Settings before you can commit or sync.")
                { style = { color = GitWaypointTheme.Subdued, fontSize = 11, whiteSpace = WhiteSpace.Normal } });
            identityBanner.Add(col);

            var go = new Button(() => SetActiveTab(Tab.Settings)) { text = "Open Settings" };
            StyleButton(go, true); go.style.flexShrink = 0;
            identityBanner.Add(go);

            root.Add(identityBanner);
        }

        void UpdateIdentityBanner()
        {
            if (identityBanner == null) return;
            var u = Env != null ? Env.User : null;
            // Only once the user object exists (don't flash the banner before git loads).
            bool show = u != null && (string.IsNullOrEmpty(u.Name) || string.IsNullOrEmpty(u.Email));
            identityBanner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // Git identity (global name+email) must be set before commit/sync are allowed.
        bool IdentityMissing()
        {
            var u = Env != null ? Env.User : null;
            return u == null || string.IsNullOrEmpty(u.Name) || string.IsNullOrEmpty(u.Email);
        }

        void SubscribeUser()
        {
            var u = Env != null ? Env.User : null;
            if (u == null || userSubscribed) return;
            u.Changed += OnUserChanged;
            userSubscribed = true;
            // Pull the current git config so the banner reflects reality rather than an empty cache.
            u.CheckAndRaiseEventsIfCacheNewer(CacheType.GitUser, lastUserEvent);
        }

        void UnsubscribeUser()
        {
            var u = Env != null ? Env.User : null;
            if (u == null || !userSubscribed) return;
            u.Changed -= OnUserChanged;
            userSubscribed = false;
        }

        void OnUserChanged(CacheUpdateEvent e)
        {
            lastUserEvent = e;
            needsRebuild = true;
            if (identitySavePending)
            {
                identitySavePending = false;
                if (IdentityMissing()) SetStatus("Couldn't save identity — see the Console", Sev.Warn);
                else SetStatus("Git identity saved", Sev.Ok);
            }
        }

        // ---- UI build ------------------------------------------------------------------------------

        void BuildUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;
            root.style.backgroundColor = GitWaypointTheme.Window;

            BuildHeader(root);
            BuildToolbar(root);
            BuildTabBar(root);
            BuildIdentityBanner(root);

            var content = new VisualElement { style = { flexGrow = 1 } };
            root.Add(content);

            tabContents[Tab.Changes] = BuildChangesTab();
            tabContents[Tab.Locks] = BuildLocksTab();
            tabContents[Tab.History] = BuildHistoryTab();
            tabContents[Tab.Branches] = BuildBranchesTab();
            tabContents[Tab.Settings] = BuildSettingsTab();
            foreach (var e in tabContents.Values) content.Add(e);

            BuildStatusStrip(root);

            // Toasts float over everything, bottom-right, above the strip. Ignore picking on the container so
            // empty space doesn't swallow clicks to the content behind it.
            toastContainer = new VisualElement();
            toastContainer.style.position = Position.Absolute;
            toastContainer.style.right = 8; toastContainer.style.bottom = 34;
            toastContainer.style.alignItems = Align.FlexEnd;
            toastContainer.pickingMode = PickingMode.Ignore;
            root.Add(toastContainer);
        }

        void BuildStatusStrip(VisualElement root)
        {
            statusStrip = Row();
            statusStrip.style.alignItems = Align.Center;
            statusStrip.style.flexShrink = 0;
            statusStrip.style.paddingLeft = 10; statusStrip.style.paddingRight = 10;
            statusStrip.style.paddingTop = 4; statusStrip.style.paddingBottom = 4;
            statusStrip.style.backgroundColor = GitWaypointTheme.Panel;
            Border(statusStrip, top: 1);
            statusIcon = new Label("") { style = { width = 12, fontSize = 11, flexShrink = 0 } };
            statusMessage = new Label("Ready") { style = { flexGrow = 1, fontSize = 11, color = GitWaypointTheme.Subdued, marginLeft = 4, whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } };
            statusTime = new Label("") { style = { fontSize = 10, color = GitWaypointTheme.Subdued, flexShrink = 0, marginLeft = 8 } };
            statusStrip.Add(statusIcon);
            statusStrip.Add(statusMessage);
            statusStrip.Add(statusTime);
            root.Add(statusStrip);
        }

        void BuildHeader(VisualElement root)
        {
            var header = Row();
            header.style.alignItems = Align.Center;
            header.style.paddingTop = 8; header.style.paddingBottom = 8;
            header.style.paddingLeft = 10; header.style.paddingRight = 10;
            header.style.backgroundColor = GitWaypointTheme.Panel;
            Border(header, bottom: 1);

            var branchBox = new VisualElement { style = { flexDirection = FlexDirection.Column, flexGrow = 1, overflow = Overflow.Hidden } };
            var branchRow = Row();
            branchRow.style.alignItems = Align.Center;
            branchRow.tooltip = "Switch branch";
            branchRow.RegisterCallback<MouseDownEvent>(_ => ShowBranchMenu());
            branchLabel = new Label("…") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 14, color = GitWaypointTheme.Text } };
            var caret = new Label(" ▾") { style = { color = GitWaypointTheme.Subdued, fontSize = 11, flexShrink = 0 } };
            branchRow.Add(branchLabel);
            branchRow.Add(caret);
            remoteLabel = new Label("") { style = { whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } };
            GitWaypointTheme.ApplyMono(remoteLabel);
            branchBox.Add(branchRow);
            branchBox.Add(remoteLabel);
            header.Add(branchBox);

            var syncBox = Row();
            syncBox.style.alignItems = Align.Center;
            syncBox.style.flexShrink = 0;
            syncLabel = new Label("") { style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold } };
            dotsLabel = new Label("") { style = { width = 18, fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, unityTextAlign = TextAnchor.MiddleLeft, color = GitWaypointTheme.Accent } };
            syncBox.Add(syncLabel);
            syncBox.Add(dotsLabel);
            header.Add(syncBox);
            root.Add(header);
        }

        void BuildToolbar(VisualElement root)
        {
            var toolbar = Row();
            toolbar.style.paddingTop = 6; toolbar.style.paddingBottom = 6;
            toolbar.style.paddingLeft = 10; toolbar.style.paddingRight = 10;
            Border(toolbar, bottom: 1);
            toolbar.Add(fetchButton = TbButton("Fetch", DoFetch));
            toolbar.Add(pullButton = TbButton("Pull", DoPull));
            toolbar.Add(pushButton = TbButton("Push", DoPush));
            toolbar.Add(Spacer());
            toolbar.Add(TbButton("Refresh", DoRefresh));
            root.Add(toolbar);
        }

        void BuildTabBar(VisualElement root)
        {
            var bar = Row();
            bar.style.backgroundColor = GitWaypointTheme.Panel;
            Border(bar, bottom: 1);
            foreach (Tab t in Enum.GetValues(typeof(Tab)))
            {
                var captured = t;
                var b = new Button(() => SetActiveTab(captured)) { text = t.ToString() };
                b.style.backgroundColor = Color.clear;
                b.style.borderTopWidth = b.style.borderLeftWidth = b.style.borderRightWidth = 0;
                b.style.borderBottomWidth = 3;
                b.style.borderBottomColor = Color.clear;
                b.style.marginLeft = b.style.marginRight = 0;
                b.style.paddingTop = b.style.paddingBottom = 9;
                b.style.paddingLeft = b.style.paddingRight = 12;
                b.style.fontSize = 13;
                b.style.color = TabInactive;
                // hover lifts the colour, but never overrides the active tab's accent
                b.RegisterCallback<MouseEnterEvent>(_ => { if (captured != activeTab) b.style.color = GitWaypointTheme.Text; });
                b.RegisterCallback<MouseLeaveEvent>(_ => { if (captured != activeTab) b.style.color = TabInactive; });
                tabButtons[t] = b;
                bar.Add(b);
            }
            root.Add(bar);
        }

        void SetActiveTab(Tab tab)
        {
            activeTab = tab;
            foreach (var kv in tabContents)
                kv.Value.style.display = kv.Key == tab ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var kv in tabButtons)
            {
                bool on = kv.Key == tab;
                kv.Value.style.color = on ? GitWaypointTheme.Accent : TabInactive;
                kv.Value.style.borderBottomColor = on ? GitWaypointTheme.Accent : Color.clear;
            }
            // Pull the data the heavy tabs need the first time they're opened.
            var repo = Repository;
            if (tab == Tab.History && repo != null && !historyRequested) { historyRequested = true; repo.Refresh(CacheType.GitLog); }
            if (tab == Tab.Branches && repo != null && !branchesRequested) { branchesRequested = true; repo.Refresh(CacheType.Branches); }
            needsRebuild = true;
        }

        void RefreshActive()
        {
            UpdateHeader();
            UpdateIdentityBanner();
            if (activeTab == Tab.Changes) RefreshChanges();
            else if (activeTab == Tab.Locks) RefreshLocks();
            else if (activeTab == Tab.History) RefreshHistory();
            else if (activeTab == Tab.Branches) RefreshBranches();
            else if (activeTab == Tab.Settings) RefreshSettingsIdentity();
        }

        // The GitUser cache loads asynchronously, after git is ready (bundled git may need extraction on
        // first launch). The Settings tab is built once with whatever the cache held then — usually empty —
        // so push the loaded name/email into the fields when they arrive and re-validate (border + Save).
        void RefreshSettingsIdentity()
        {
            if (settingsNameField == null) return;
            var u = Env != null ? Env.User : null;
            if (u != null)
            {
                // Don't overwrite a value the user is currently editing.
                if (settingsNameField.focusController == null || settingsNameField.focusController.focusedElement != settingsNameField)
                    settingsNameField.SetValueWithoutNotify(u.Name ?? "");
                if (settingsEmailField.focusController == null || settingsEmailField.focusController.focusedElement != settingsEmailField)
                    settingsEmailField.SetValueWithoutNotify(u.Email ?? "");
            }
            settingsValidateUser?.Invoke();
        }

        // ---- Header --------------------------------------------------------------------------------

        void UpdateHeader()
        {
            var repo = Repository;
            if (repo == null)
            {
                branchLabel.text = "Not connected";
                remoteLabel.text = "";
                if (runningOp == null) { syncLabel.text = "Open a Git project to get started"; syncLabel.style.color = GitWaypointTheme.Subdued; }
                SetRemoteButtonsEnabled(false);
                return;
            }
            bool hasRemote = repo.CurrentRemote.HasValue;
            branchLabel.text = string.IsNullOrEmpty(repo.CurrentBranchName) ? "(no branch)" : repo.CurrentBranchName;
            remoteLabel.text = hasRemote ? repo.CurrentRemote.Value.Url ?? "" : "no remote";
            if (runningOp == null) UpdateSyncLabel(repo.CurrentAhead, repo.CurrentBehind, hasRemote);
            SetRemoteButtonsEnabled(hasRemote && runningOp == null && !IdentityMissing());
        }

        void ShowBranchMenu()
        {
            var repo = Repository; if (repo == null) return;
            var menu = new GenericMenu();
            var current = repo.CurrentBranchName;
            var locals = repo.LocalBranches ?? new GitBranch[0];
            if (locals.Length == 0) menu.AddDisabledItem(new GUIContent("No branches"));
            foreach (var b in locals.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var name = b.Name;
                menu.AddItem(new GUIContent(name), name == current, () => { if (name != current) SwitchBranch(name); });
            }
            menu.ShowAsContext();
        }

        void UpdateSyncLabel(int ahead, int behind, bool hasRemote)
        {
            if (!hasRemote) { syncLabel.text = "No remote"; syncLabel.style.color = GitWaypointTheme.Subdued; return; }
            if (behind > 0 && ahead > 0) { syncLabel.text = "↑" + ahead + " ↓" + behind + " diverged"; syncLabel.style.color = GitWaypointTheme.Outdated; }
            else if (behind > 0) { syncLabel.text = "↓" + behind + " to pull"; syncLabel.style.color = GitWaypointTheme.Outdated; }
            else if (ahead > 0) { syncLabel.text = "↑" + ahead + " to push"; syncLabel.style.color = GitWaypointTheme.Accent; }
            else { syncLabel.text = "✓ Up to date"; syncLabel.style.color = GitWaypointTheme.UpToDate; }
        }

        // ---- Changes tab ---------------------------------------------------------------------------

        VisualElement BuildChangesTab()
        {
            var tab = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column, display = DisplayStyle.None } };

            var topRow = Row();
            topRow.style.alignItems = Align.Center;
            topRow.style.paddingLeft = 10; topRow.style.paddingRight = 10;
            topRow.style.paddingTop = 8; topRow.style.paddingBottom = 6;
            searchField = new TextField { style = { flexGrow = 1 } };
            SetPlaceholder(searchField, "Search files…");
            Roomy(searchField, 24);
            searchField.RegisterValueChangedCallback(e => { searchText = e.newValue ?? ""; RefreshChanges(); });
            topRow.Add(searchField);

            changesFilterButton = new Button(ShowChangesFilterMenu) { text = "All changes  ▾" };
            changesFilterButton.style.height = 24;
            changesFilterButton.style.marginLeft = 8; changesFilterButton.style.marginRight = 0; changesFilterButton.style.marginTop = 0; changesFilterButton.style.marginBottom = 0;
            changesFilterButton.style.paddingLeft = 10; changesFilterButton.style.paddingRight = 10;
            changesFilterButton.style.backgroundColor = GitWaypointTheme.Elevated;
            changesFilterButton.style.color = GitWaypointTheme.Text;
            changesFilterButton.style.borderTopWidth = changesFilterButton.style.borderBottomWidth = changesFilterButton.style.borderLeftWidth = changesFilterButton.style.borderRightWidth = 1;
            changesFilterButton.style.borderTopColor = changesFilterButton.style.borderBottomColor = changesFilterButton.style.borderLeftColor = changesFilterButton.style.borderRightColor = GitWaypointTheme.Border;
            GitWaypointTheme.Round(changesFilterButton, 6);
            topRow.Add(changesFilterButton);
            tab.Add(topRow);

            changesSelRow = Row();
            changesSelRow.style.alignItems = Align.Center;
            changesSelRow.style.paddingLeft = 10; changesSelRow.style.paddingRight = 10;
            changesSelRow.style.paddingBottom = 4;
            selectAllToggle = new Toggle { value = true };
            selectAllToggle.RegisterValueChangedCallback(e => SetAllChecked(e.newValue));
            changesSelRow.Add(selectAllToggle);
            changesSelRow.Add(new Label("Select all") { style = { marginLeft = 4, color = GitWaypointTheme.Text } });
            changesSelRow.Add(Spacer());
            changesCount = new Label("") { style = { color = GitWaypointTheme.Subdued, fontSize = 11 } };
            changesSelRow.Add(changesCount);
            discardAllButton = new Button(DiscardAllChanges) { text = "Discard all" };
            StyleDangerButton(discardAllButton);
            discardAllButton.style.marginLeft = 8;
            changesSelRow.Add(discardAllButton);
            tab.Add(changesSelRow);

            outdatedBanner = new VisualElement();
            outdatedBanner.style.flexDirection = FlexDirection.Row;
            outdatedBanner.style.alignItems = Align.Center;
            outdatedBanner.style.display = DisplayStyle.None;
            outdatedBanner.style.marginLeft = 10; outdatedBanner.style.marginRight = 10; outdatedBanner.style.marginBottom = 6;
            outdatedBanner.style.paddingTop = 8; outdatedBanner.style.paddingBottom = 8;
            outdatedBanner.style.paddingLeft = 10; outdatedBanner.style.paddingRight = 10;
            outdatedBanner.style.backgroundColor = new Color(GitWaypointTheme.Outdated.r, GitWaypointTheme.Outdated.g, GitWaypointTheme.Outdated.b, 0.10f);
            GitWaypointTheme.Round(outdatedBanner, 6);
            outdatedBanner.style.borderTopWidth = outdatedBanner.style.borderBottomWidth = outdatedBanner.style.borderLeftWidth = outdatedBanner.style.borderRightWidth = 1;
            var bannerBorder = new Color(GitWaypointTheme.Outdated.r, GitWaypointTheme.Outdated.g, GitWaypointTheme.Outdated.b, 0.5f);
            outdatedBanner.style.borderTopColor = outdatedBanner.style.borderBottomColor = outdatedBanner.style.borderLeftColor = outdatedBanner.style.borderRightColor = bannerBorder;

            outdatedBanner.Add(new Label("⚠") { style = { color = GitWaypointTheme.Outdated, fontSize = 16, marginRight = 8, unityTextAlign = TextAnchor.MiddleCenter } });

            var bannerCol = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column } };
            outdatedBannerLabel = new Label("") { style = { color = GitWaypointTheme.Outdated, fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal } };
            bannerCol.Add(outdatedBannerLabel);
            bannerCol.Add(new Label("Pull to get the latest changes before pushing.") { style = { color = GitWaypointTheme.Subdued, fontSize = 11, whiteSpace = WhiteSpace.Normal } });
            outdatedBanner.Add(bannerCol);

            var bannerPull = new Button(() => DoPull()) { text = "Pull" };
            StyleButton(bannerPull, false);
            bannerPull.style.marginLeft = 8;
            outdatedBanner.Add(bannerPull);
            tab.Add(outdatedBanner);

            changesList = new ScrollView { style = { flexGrow = 1, flexShrink = 1, minHeight = 0 } };
            tab.Add(changesList);

            emptyLabel = new Label("No changes. Your working tree is clean.")
            {
                style = { flexGrow = 1, color = GitWaypointTheme.Subdued, unityTextAlign = TextAnchor.MiddleCenter, paddingTop = 24, paddingBottom = 24, whiteSpace = WhiteSpace.Normal, display = DisplayStyle.None }
            };
            tab.Add(emptyLabel);

            var panel = new VisualElement();
            panel.style.flexShrink = 0;
            Border(panel, top: 1);
            panel.style.backgroundColor = GitWaypointTheme.Panel;
            panel.style.paddingTop = 12; panel.style.paddingBottom = 12;
            panel.style.paddingLeft = 12; panel.style.paddingRight = 12;

            commitMessage = new TextField();
            SetPlaceholder(commitMessage, "Commit summary");
            Roomy(commitMessage, 28);
            commitMessage.RegisterValueChangedCallback(_ => UpdateCommitEnabled());
            panel.Add(commitMessage);

            commitDescription = new TextField { multiline = true };
            commitDescription.style.marginTop = 8;
            commitDescription.style.whiteSpace = WhiteSpace.Normal;
            SetPlaceholder(commitDescription, "Description (optional)");
            Roomy(commitDescription, 60);
            panel.Add(commitDescription);

            var actions = Row();
            actions.style.marginTop = 10;
            actions.style.justifyContent = Justify.FlexEnd;
            commitButton = new Button(() => Commit(false)) { text = "Commit" };
            commitPushButton = new Button(() => Commit(true)) { text = "Commit & Push" };
            StyleButton(commitButton, false);
            StyleButton(commitPushButton, true);
            commitButton.style.marginRight = 6;
            actions.Add(commitButton);
            actions.Add(commitPushButton);
            panel.Add(actions);
            tab.Add(panel);

            return tab;
        }

        static string FilterLabel(ChangesFilter f)
        {
            return f == ChangesFilter.All ? "All changes" : f.ToString();
        }

        void ShowChangesFilterMenu()
        {
            var menu = new GenericMenu();
            foreach (ChangesFilter f in Enum.GetValues(typeof(ChangesFilter)))
            {
                var captured = f;
                menu.AddItem(new GUIContent(FilterLabel(f)), changesFilter == f, () =>
                {
                    changesFilter = captured;
                    changesFilterButton.text = FilterLabel(captured) + "  ▾";
                    RefreshChanges();
                });
            }
            menu.ShowAsContext();
        }

        bool MatchesFilter(GitStatusEntry entry, HashSet<SPath> lockedByMe, Dictionary<SPath, string> lockedByOther)
        {
            switch (changesFilter)
            {
                case ChangesFilter.Modified: return entry.Status == GitFileStatus.Modified || entry.Status == GitFileStatus.TypeChange;
                case ChangesFilter.Added: return entry.Status == GitFileStatus.Added || entry.Status == GitFileStatus.Untracked;
                case ChangesFilter.Deleted: return entry.Status == GitFileStatus.Deleted;
                case ChangesFilter.Outdated: return IsOutdated(entry.Path);
                case ChangesFilter.Locked: { var sp = entry.Path.ToSPath(); return lockedByMe.Contains(sp) || lockedByOther.ContainsKey(sp); }
                default: return true;
            }
        }

        // ---- LFS detection (for the "LFS" chip) ----------------------------------------------------
        // Whether a file is stored in Git LFS, by matching its extension against the `*.ext lfs` /
        // `*.ext filter=lfs` patterns in the repo's .gitattributes. Parsed once and cached until the file
        // changes - cheap enough to call per row, and no per-file git invocation.
        static HashSet<string> lfsExtensions;
        static string lfsAttrsPath;
        static System.DateTime lfsAttrsStamp;

        void EnsureLfsExtensions()
        {
            var repo = Repository;
            if (repo == null) { lfsExtensions = null; return; }
            string p;
            try { p = repo.LocalPath.Combine(".gitattributes").ToString(); }
            catch { lfsExtensions = new HashSet<string>(); return; }

            try
            {
                var info = new System.IO.FileInfo(p);
                if (!info.Exists) { lfsExtensions = new HashSet<string>(); lfsAttrsPath = p; return; }
                if (lfsExtensions != null && p == lfsAttrsPath && info.LastWriteTimeUtc == lfsAttrsStamp) return;

                lfsAttrsPath = p; lfsAttrsStamp = info.LastWriteTimeUtc;
                var set = new HashSet<string>();
                foreach (var line in System.IO.File.ReadAllLines(p))
                {
                    var l = line.Trim();
                    if (l.Length == 0 || l[0] == '#' || l[0] == '[') continue;
                    var parts = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    if (!parts.Skip(1).Any(t => t == "lfs" || t == "filter=lfs")) continue;
                    if (parts[0].StartsWith("*."))
                        set.Add(parts[0].Substring(2).ToLowerInvariant());
                }
                lfsExtensions = set;
            }
            catch { lfsExtensions = new HashSet<string>(); }
        }

        bool IsLfsTracked(string repoRelPath)
        {
            if (lfsExtensions == null || lfsExtensions.Count == 0) return false;
            var ext = System.IO.Path.GetExtension(repoRelPath);
            return !string.IsNullOrEmpty(ext) && lfsExtensions.Contains(ext.TrimStart('.').ToLowerInvariant());
        }

        void RefreshChanges()
        {
            var repo = Repository;
            if (repo == null)
            {
                changesList.Clear();
                emptyLabel.text = "No repository. Open a project that uses Git.";
                emptyLabel.style.display = DisplayStyle.Flex;
                changesList.style.display = DisplayStyle.None;
                changesCount.text = "";
                if (discardAllButton != null) discardAllButton.SetEnabled(false);
                outdatedBanner.style.display = DisplayStyle.None;
                UpdateCommitEnabled();
                return;
            }

            var changes = (repo.CurrentChanges ?? new List<GitStatusEntry>())
                .Where(x => x.Status != GitFileStatus.Ignored)
                .ToList();

            var me = ProjectWindowInterface.CurrentUsername;
            var locks = repo.CurrentLocks ?? new List<GitLock>();
            var lockedByOther = locks.Where(l => string.IsNullOrEmpty(me) || l.Owner.Name != me).ToDictionary(l => l.Path, l => l.Owner.Name);
            var lockedByMe = new HashSet<SPath>(locks.Where(l => !string.IsNullOrEmpty(me) && l.Owner.Name == me).Select(l => l.Path));
            var lockByPath = locks.GroupBy(l => l.Path).ToDictionary(g => g.Key, g => g.First());

            var present = new HashSet<string>(changes.Select(c => c.Path));
            foreach (var c in changes)
                if (knownPaths.Add(c.Path)) checkedPaths.Add(c.Path);
            checkedPaths.RemoveWhere(p => !present.Contains(p));
            knownPaths.RemoveWhere(p => !present.Contains(p));

            EnsureLfsExtensions();

            var visible = changes
                .Where(c => string.IsNullOrEmpty(searchText) || c.Path.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(c => MatchesFilter(c, lockedByMe, lockedByOther))
                .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int outdatedCount = 0;
            changesList.Clear();
            foreach (var entry in visible)
            {
                bool outdated = IsOutdated(entry.Path);
                if (outdated) outdatedCount++;
                changesList.Add(BuildChangeRow(entry, lockedByMe, lockedByOther, outdated, lockByPath));
            }

            // "Select all" only makes sense when there's something to select.
            changesSelRow.style.display = changes.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            bool any = visible.Count > 0;
            emptyLabel.text = changes.Count == 0 ? "No changes. Your working tree is clean." : "No files match your search.";
            emptyLabel.style.display = any ? DisplayStyle.None : DisplayStyle.Flex;
            changesList.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            changesCount.text = changes.Count == 0 ? "" : changes.Count + " changed" + (outdatedCount > 0 ? " · " + outdatedCount + " outdated" : "");
            if (discardAllButton != null) discardAllButton.SetEnabled(changes.Count > 0 && runningOp == null && !isBusy);

            if (outdatedCount > 0)
            {
                outdatedBanner.style.display = DisplayStyle.Flex;
                outdatedBannerLabel.text = outdatedCount == 1
                    ? "1 file is outdated"
                    : outdatedCount + " files are outdated";
            }
            else outdatedBanner.style.display = DisplayStyle.None;

            UpdateCommitEnabled();
        }

        VisualElement BuildChangeRow(GitStatusEntry entry, HashSet<SPath> lockedByMe, Dictionary<SPath, string> lockedByOther, bool outdated, Dictionary<SPath, GitLock> lockByPath)
        {
            var sp = entry.Path.ToSPath();
            var row = Row();
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 10; row.style.paddingRight = 10;
            float vpad = CompactDensity ? 1 : 3;
            row.style.paddingTop = vpad; row.style.paddingBottom = vpad;
            Border(row, bottom: 1);

            // Right-click context menu, like the classic Changes view.
            GitLock fileLock; bool hasLock = lockByPath.TryGetValue(sp, out fileLock);
            bool isMine = lockedByMe.Contains(sp);
            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Show Diff", _ => ShowDiff(entry));
                evt.menu.AppendSeparator();
                if (!hasLock) evt.menu.AppendAction("Lock", _ => RequestLock(sp));
                else if (isMine) evt.menu.AppendAction("Unlock", _ => ReleaseLock(fileLock, false));
                else evt.menu.AppendAction("Force unlock (" + fileLock.Owner.Name + ")", _ => ForceUnlockConfirm(fileLock));
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Discard changes", _ => DiscardChanges(entry));
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Show in Project", _ => PingAsset(entry.Path));
                evt.menu.AppendAction("Copy Path", _ => EditorGUIUtility.systemCopyBuffer = entry.Path);
            }));

            var toggle = new Toggle { value = checkedPaths.Contains(entry.Path) };
            toggle.RegisterValueChangedCallback(e =>
            {
                if (e.newValue) checkedPaths.Add(entry.Path); else checkedPaths.Remove(entry.Path);
                UpdateCommitEnabled();
            });
            row.Add(toggle);

            string letter; Color color;
            GitWaypointTheme.DiffBadge(entry.Status, out letter, out color);
            var badge = GitWaypointTheme.BadgeSquare(letter, color);
            badge.style.marginLeft = 4; badge.style.marginRight = 6;
            row.Add(badge);

            row.Add(NamePathBox(sp, IsLfsTracked(entry.Path)));

            string pending = ProjectWindowInterface.IsPendingUnlock(entry.Path) ? "Releasing…"
                           : ProjectWindowInterface.IsPendingLock(entry.Path) ? "Locking…" : null;
            if (pending != null) { var c = GitWaypointTheme.Chip(pending, GitWaypointTheme.Accent); c.style.marginLeft = 6; row.Add(c); }
            else if (outdated) { var c = GitWaypointTheme.Chip("Outdated", GitWaypointTheme.Outdated); c.style.marginLeft = 6; row.Add(c); }
            if (lockedByMe.Contains(sp)) { var c = GitWaypointTheme.Chip("Locked by you", GitWaypointTheme.UpToDate); c.style.marginLeft = 6; row.Add(c); }
            else if (lockedByOther.ContainsKey(sp)) { var c = GitWaypointTheme.Chip(lockedByOther[sp] + " · locked", GitWaypointTheme.Conflict); c.style.marginLeft = 6; row.Add(c); }

            return row;
        }

        VisualElement NamePathBox(SPath sp, bool lfs)
        {
            // Fixed height (not min): rows with a path line are naturally taller than rows without, so a
            // min-height alone still leaves them uneven. A fixed box height makes every row identical and
            // the name centres vertically when there's no path (e.g. files in the repo root).
            var box = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column, justifyContent = Justify.Center, overflow = Overflow.Hidden } };
            box.style.height = CompactDensity ? 28 : 34;

            var nameRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            nameRow.Add(new Label(sp.FileName) { style = { color = GitWaypointTheme.Text, fontSize = 12, flexShrink = 1, whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } });
            if (lfs)
            {
                var lfsChip = GitWaypointTheme.Chip("LFS", GitWaypointTheme.Accent);
                lfsChip.style.marginLeft = 6; lfsChip.style.flexShrink = 0;
                nameRow.Add(lfsChip);
            }
            box.Add(nameRow);

            string dir = "";
            try { var parent = sp.Parent; if (parent.IsInitialized && !parent.IsEmpty) dir = parent.ToString(SlashMode.Forward) + "/"; } catch { }
            if (!string.IsNullOrEmpty(dir))
            {
                var pathLabel = new Label(dir) { style = { whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } };
                GitWaypointTheme.ApplyMono(pathLabel);
                pathLabel.style.fontSize = 10;
                box.Add(pathLabel);
            }
            return box;
        }

        // ---- Locks tab (native) --------------------------------------------------------------------

        VisualElement BuildLocksTab()
        {
            var tab = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column, display = DisplayStyle.None } };

            lockStatsRow = Row();
            lockStatsRow.style.paddingLeft = 10; lockStatsRow.style.paddingRight = 10;
            lockStatsRow.style.paddingTop = 8; lockStatsRow.style.paddingBottom = 6;
            tab.Add(lockStatsRow);

            var filterRow = Row();
            filterRow.style.alignItems = Align.Center;
            filterRow.style.paddingLeft = 10; filterRow.style.paddingRight = 10;
            filterRow.style.paddingBottom = 6;
            lockSearchField = new TextField { style = { flexGrow = 1, marginLeft = 0, marginRight = 8 } };
            SetPlaceholder(lockSearchField, "Search locked files…");
            Roomy(lockSearchField, 24);
            lockSearchField.RegisterValueChangedCallback(e => { lockSearchText = e.newValue ?? ""; RefreshLocks(); });
            filterRow.Add(lockSearchField);
            foreach (LockFilter f in Enum.GetValues(typeof(LockFilter)))
            {
                var captured = f;
                // A segmented toggle, not a normal button: no hover reset (that was fighting the selected
                // colour and making it blink). RefreshLocks owns the selected/unselected colours.
                var b = new Button(() => { lockFilter = captured; RefreshLocks(); }) { text = f.ToString() };
                b.style.height = 26;
                b.style.paddingLeft = 14; b.style.paddingRight = 14;
                b.style.marginLeft = 4; b.style.marginRight = 0; b.style.marginTop = 0; b.style.marginBottom = 0;
                b.style.borderTopWidth = b.style.borderBottomWidth = b.style.borderLeftWidth = b.style.borderRightWidth = 1;
                b.style.borderTopColor = b.style.borderBottomColor = b.style.borderLeftColor = b.style.borderRightColor = GitWaypointTheme.Border;
                GitWaypointTheme.Round(b, 7);
                lockFilterButtons[f] = b;
                filterRow.Add(b);
            }
            tab.Add(filterRow);

            locksList = new ScrollView { style = { flexGrow = 1, flexShrink = 1, minHeight = 0 } };
            tab.Add(locksList);

            locksEmpty = new Label("No locks match your filters.")
            {
                style = { flexGrow = 1, color = GitWaypointTheme.Subdued, unityTextAlign = TextAnchor.MiddleCenter, paddingTop = 24, paddingBottom = 24, whiteSpace = WhiteSpace.Normal, display = DisplayStyle.None }
            };
            tab.Add(locksEmpty);

            // Footer: automatic-locking status + shortcut to its settings.
            var footer = Row();
            footer.style.alignItems = Align.Center; footer.style.flexShrink = 0;
            footer.style.paddingLeft = 10; footer.style.paddingRight = 10; footer.style.paddingTop = 8; footer.style.paddingBottom = 8;
            footer.style.backgroundColor = GitWaypointTheme.Panel;
            Border(footer, top: 1);
            lockAutoLabel = new Label("") { style = { flexGrow = 1, color = GitWaypointTheme.Subdued, fontSize = 11 } };
            footer.Add(lockAutoLabel);
            var lockSettingsBtn = new Button(() => SetActiveTab(Tab.Settings)) { text = "Locking settings" };
            StyleButton(lockSettingsBtn, false); lockSettingsBtn.style.flexShrink = 0;
            footer.Add(lockSettingsBtn);
            tab.Add(footer);

            return tab;
        }

        void RefreshLocks()
        {
            if (locksList == null) return;
            var repo = Repository;
            var locks = repo != null ? (repo.CurrentLocks ?? new List<GitLock>()) : new List<GitLock>();
            var me = ProjectWindowInterface.CurrentUsername;

            int mine = locks.Count(l => !string.IsNullOrEmpty(me) && l.Owner.Name == me);
            int team = locks.Count - mine;
            int stale = locks.Count(l => IsStale(l));

            lockStatsRow.Clear();
            lockStatsRow.Add(StatCard("My locks", mine, GitWaypointTheme.UpToDate, false));
            lockStatsRow.Add(StatCard("Team locks", team, GitWaypointTheme.Accent, false));
            lockStatsRow.Add(StatCard("Stale > " + StaleHours + "h", stale, GitWaypointTheme.Outdated, true));

            foreach (var kv in lockFilterButtons)
            {
                bool on = kv.Key == lockFilter;
                kv.Value.style.backgroundColor = on ? GitWaypointTheme.Accent : GitWaypointTheme.Elevated;
                kv.Value.style.color = on ? Color.white : GitWaypointTheme.Text;
            }

            var visible = locks
                .Where(l => lockFilter == LockFilter.All
                            || (lockFilter == LockFilter.Mine && !string.IsNullOrEmpty(me) && l.Owner.Name == me)
                            || (lockFilter == LockFilter.Team && (string.IsNullOrEmpty(me) || l.Owner.Name != me)))
                .Where(l => string.IsNullOrEmpty(lockSearchText) || l.Path.ToString().IndexOf(lockSearchText, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(l => l.LockedAt)
                .ToList();

            locksList.Clear();
            // Optimistic "Locking…" rows for acquires in flight that aren't in the lock list yet (shared
            // with the Project window, so a lock requested by editing a file shows here too).
            var realPaths = new HashSet<string>(locks.Select(l => l.Path.ToString()));
            foreach (var pa in ProjectWindowInterface.PendingLockAssetPaths())
                if (ProjectWindowInterface.IsPendingLock(pa) && !realPaths.Contains(pa))
                    locksList.Add(BuildPendingLockRow(pa, "Locking…"));
            foreach (var lck in visible)
            {
                var ap = lck.Path.ToString();
                string busy = ProjectWindowInterface.IsPendingUnlock(ap) ? "Releasing…"
                            : ProjectWindowInterface.IsPendingLock(ap) ? "Locking…" : null;
                locksList.Add(BuildLockRow(lck, !string.IsNullOrEmpty(me) && lck.Owner.Name == me, busy));
            }

            bool any = locksList.childCount > 0;
            locksEmpty.text = locks.Count == 0 ? "No active locks." : "No locks match your filters.";
            locksEmpty.style.display = any ? DisplayStyle.None : DisplayStyle.Flex;
            locksList.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;

            bool autoOn = LfsLocksModificationProcessor.AutoLockEnabled;
            lockAutoLabel.text = "● Automatic locking: " + (autoOn ? "On" : "Off");
            lockAutoLabel.style.color = autoOn ? GitWaypointTheme.UpToDate : GitWaypointTheme.Subdued;
        }

        VisualElement BuildPendingLockRow(string path, string status)
        {
            var sp = path.ToSPath();
            var row = Row();
            row.style.alignItems = Align.Center;
            row.style.minHeight = 36;
            row.style.paddingLeft = 10; row.style.paddingRight = 10;
            Border(row, bottom: 1);
            row.Add(Avatar(ProjectWindowInterface.CurrentUsername, GitWaypointTheme.UpToDate));
            var box = NamePathBox(sp, IsLfsTracked(sp.ToString())); box.style.marginLeft = 6; row.Add(box);
            row.Add(new Label(status) { style = { color = GitWaypointTheme.Accent, fontSize = 11, marginLeft = 6, flexShrink = 0 } });
            return row;
        }

        VisualElement BuildLockRow(GitLock lck, bool mine, string busy)
        {
            var row = Row();
            row.style.alignItems = Align.Center;
            row.style.minHeight = 36;
            row.style.paddingLeft = 10; row.style.paddingRight = 10;
            float vpad = CompactDensity ? 2 : 4;
            row.style.paddingTop = vpad; row.style.paddingBottom = vpad;
            Border(row, bottom: 1);

            row.Add(Avatar(lck.Owner.Name, mine ? GitWaypointTheme.UpToDate : GitWaypointTheme.Accent));

            var box = NamePathBox(lck.Path, IsLfsTracked(lck.Path.ToString()));
            box.style.marginLeft = 6;
            row.Add(box);

            var meta = new VisualElement { style = { flexDirection = FlexDirection.Column, alignItems = Align.FlexEnd, marginLeft = 6, marginRight = 6, flexShrink = 0 } };
            meta.Add(new Label(mine ? "you" : lck.Owner.Name) { style = { color = GitWaypointTheme.Text, fontSize = 11 } });
            var ageLabel = new Label(Ago(lck.LockedAt)) { style = { fontSize = 10, color = IsStale(lck) ? GitWaypointTheme.Outdated : GitWaypointTheme.Subdued } };
            meta.Add(ageLabel);
            row.Add(meta);

            if (busy != null)
            {
                row.Add(new Label(busy) { style = { color = GitWaypointTheme.Accent, fontSize = 11, flexShrink = 0 } });
            }
            else if (mine)
            {
                var b = new Button(() => ReleaseLock(lck, false)) { text = "Release" };
                StyleButton(b, false);
                b.style.flexShrink = 0;
                row.Add(b);
            }
            else
            {
                var b = new Button(() =>
                {
                    if (EditorUtility.DisplayDialog("Force unlock",
                        "Force-unlock " + lck.Owner.Name + "'s lock on " + lck.Path.FileName + "?\nThey may lose work if they're still editing it.",
                        "Force unlock", "Cancel"))
                        ReleaseLock(lck, true);
                }) { text = "Force unlock" };
                StyleButton(b, false);
                b.style.flexShrink = 0;
                row.Add(b);
            }
            return row;
        }

        void ReleaseLock(GitLock lck, bool force)
        {
            ProjectWindowInterface.ReleaseLock(lck.Path.ToString(), force, err => NotifyFailure("Unlock", new Exception(err)));
            needsRebuild = true;
        }

        static bool IsStale(GitLock l) => l.LockedAt != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - l.LockedAt).TotalHours >= StaleHours;

        static string Ago(DateTimeOffset t)
        {
            if (t == DateTimeOffset.MinValue) return "";
            var span = DateTimeOffset.UtcNow - t;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + "m ago";
            if (span.TotalDays < 1) return (int)span.TotalHours + "h ago";
            return (int)span.TotalDays + "d ago";
        }

        VisualElement StatCard(string label, int count, Color accent, bool last)
        {
            var card = new VisualElement();
            card.style.flexGrow = 1;
            card.style.marginRight = last ? 0 : 6;
            card.style.paddingTop = 6; card.style.paddingBottom = 6;
            card.style.paddingLeft = 8; card.style.paddingRight = 8;
            card.style.backgroundColor = GitWaypointTheme.Elevated;
            GitWaypointTheme.Round(card, 6);
            var n = new Label(count.ToString()) { style = { color = accent, fontSize = 18, unityFontStyleAndWeight = FontStyle.Bold } };
            var l = new Label(label) { style = { color = GitWaypointTheme.Subdued, fontSize = 10 } };
            card.Add(n); card.Add(l);
            return card;
        }

        static VisualElement Avatar(string name, Color color, float size = 22)
        {
            var a = new VisualElement { style = { width = size, height = size, flexShrink = 0, alignItems = Align.Center, justifyContent = Justify.Center } };
            a.style.backgroundColor = new Color(color.r, color.g, color.b, 0.25f);
            GitWaypointTheme.Round(a, size / 2f);
            a.Add(new Label(Initials(name)) { style = { color = color, fontSize = size <= 16 ? 8 : 10, unityFontStyleAndWeight = FontStyle.Bold } });
            return a;
        }

        // Up to two initials: first letters of the first two words, or the first two letters of a single word.
        static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Trim().Split(new[] { ' ', '.', '_', '-', '@' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return (parts[0].Substring(0, 1) + parts[1].Substring(0, 1)).ToUpperInvariant();
            var w = parts.Length == 1 ? parts[0] : name.Trim();
            return (w.Length >= 2 ? w.Substring(0, 2) : w).ToUpperInvariant();
        }

        // ---- History tab (native) ------------------------------------------------------------------

        VisualElement BuildHistoryTab()
        {
            var tab = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column, display = DisplayStyle.None } };
            historyHeader = new Label("") { style = { color = GitWaypointTheme.Subdued, fontSize = 11, paddingLeft = 10, paddingRight = 10, paddingTop = 8, paddingBottom = 6 } };
            Border(historyHeader, bottom: 1);
            tab.Add(historyHeader);
            historyList = new ScrollView { style = { flexGrow = 1, flexShrink = 1, minHeight = 0 } };
            tab.Add(historyList);
            historyEmpty = new Label("No history yet.")
            {
                style = { flexGrow = 1, color = GitWaypointTheme.Subdued, unityTextAlign = TextAnchor.MiddleCenter, paddingTop = 24, paddingBottom = 24, display = DisplayStyle.None }
            };
            tab.Add(historyEmpty);
            historyDetail = new VisualElement();
            historyDetail.style.flexShrink = 0;
            historyDetail.style.display = DisplayStyle.None;
            historyDetail.style.backgroundColor = GitWaypointTheme.Panel;
            historyDetail.style.paddingTop = 10; historyDetail.style.paddingBottom = 10;
            historyDetail.style.paddingLeft = 12; historyDetail.style.paddingRight = 12;
            Border(historyDetail, top: 1);
            tab.Add(historyDetail);
            return tab;
        }

        void RefreshHistory()
        {
            if (historyList == null) return;
            var repo = Repository;
            var log = repo != null ? (repo.CurrentLog ?? new List<GitLogEntry>()) : new List<GitLogEntry>();

            var branch = repo != null && !string.IsNullOrEmpty(repo.CurrentBranchName) ? repo.CurrentBranchName : "(current branch)";
            string sync = "";
            if (repo != null && repo.CurrentRemote.HasValue)
            {
                if (repo.CurrentBehind > 0) sync += "  ·  ↓" + repo.CurrentBehind + " behind";
                if (repo.CurrentAhead > 0) sync += "  ·  ↑" + repo.CurrentAhead + " ahead";
                if (repo.CurrentBehind == 0 && repo.CurrentAhead == 0) sync += "  ·  up to date";
            }
            historyHeader.text = "Commits on " + branch + sync;

            historyList.Clear();
            // Incoming commits (to pull) first, so "what's about to land" reads top-down into your current HEAD.
            if (incomingLog.Count > 0)
            {
                historyList.Add(SectionHeader("Incoming · " + incomingLog.Count + " to pull"));
                foreach (var e in incomingLog)
                    historyList.Add(BuildCommitRow(e, false, branch, incoming: true));
            }
            for (int i = 0; i < log.Count; i++)
                historyList.Add(BuildCommitRow(log[i], i == 0, branch));

            bool any = log.Count > 0 || incomingLog.Count > 0;
            historyEmpty.style.display = any ? DisplayStyle.None : DisplayStyle.Flex;
            historyList.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;

            var selectable = incomingLog.Concat(log);
            if (selectedCommit != null && selectable.Any(e => e.CommitID == selectedCommit))
                ShowCommitDetail(selectable.First(e => e.CommitID == selectedCommit));
            else { historyDetail.style.display = DisplayStyle.None; selectedCommit = null; }

            RefreshIncoming(repo);
        }

        // Load the commits on the upstream we haven't pulled (HEAD..@{u}) for the Incoming section. Only when
        // we're actually behind (skips the git call otherwise / when there's no upstream). Cached; rebuilds
        // only when the set changes, so it doesn't flicker on every history refresh.
        void RefreshIncoming(IRepository repo)
        {
            bool behind = repo != null && repo.CurrentRemote.HasValue && repo.CurrentBehind > 0;
            if (!behind)
            {
                if (incomingLog.Count > 0) { incomingLog = new List<GitLogEntry>(); needsRebuild = true; }
                return;
            }
            var mgr = Manager;
            if (mgr == null || mgr.GitClient == null || incomingInFlight) return;
            incomingInFlight = true;
            mgr.GitClient.LogRange("HEAD..@{u}").FinallyInUI((s, e, list) =>
            {
                incomingInFlight = false;
                if (!s || list == null) return;
                if (!SameCommits(incomingLog, list)) { incomingLog = list; needsRebuild = true; }
            }).Start();
        }

        static bool SameCommits(List<GitLogEntry> a, List<GitLogEntry> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].CommitID != b[i].CommitID) return false;
            return true;
        }

        VisualElement BuildCommitRow(GitLogEntry entry, bool isHead, string branchName, bool incoming = false)
        {
            bool selected = entry.CommitID == selectedCommit;
            var row = Row();
            row.style.alignItems = Align.FlexStart;
            row.style.paddingLeft = 10; row.style.paddingRight = 10;
            row.style.paddingTop = 6; row.style.paddingBottom = 6;
            row.style.backgroundColor = selected ? new Color(GitWaypointTheme.Accent.r, GitWaypointTheme.Accent.g, GitWaypointTheme.Accent.b, 0.15f) : Color.clear;
            Border(row, bottom: 1);
            row.RegisterCallback<MouseDownEvent>(_ => { selectedCommit = entry.CommitID; RefreshHistory(); });

            // graph node dot: accent for HEAD, outdated/orange for incoming (to-pull), subdued otherwise
            var dot = new VisualElement { style = { width = 8, height = 8, marginTop = 4, marginRight = 8, flexShrink = 0 } };
            dot.style.backgroundColor = incoming ? GitWaypointTheme.Outdated : (isHead ? GitWaypointTheme.Accent : GitWaypointTheme.Subdued);
            GitWaypointTheme.Round(dot, 4);
            row.Add(dot);

            var col = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column, overflow = Overflow.Hidden } };
            // summary + badges
            var titleRow = Row();
            titleRow.style.alignItems = Align.Center;
            // minWidth:0 lets the summary shrink below its content width so it ellipsizes instead of pushing
            // the ref pills off the row; the pills get flexShrink:0 so they always stay whole.
            titleRow.Add(new Label(entry.summary) { style = { flexGrow = 1, flexShrink = 1, minWidth = 0, color = GitWaypointTheme.Text, fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } });
            if (incoming)
            {
                var pull = GitWaypointTheme.Chip("↓ to pull", GitWaypointTheme.Outdated); pull.style.marginLeft = 4; pull.style.flexShrink = 0; titleRow.Add(pull);
            }
            else if (isHead)
            {
                var head = GitWaypointTheme.Chip("HEAD", GitWaypointTheme.Accent); head.style.marginLeft = 4; head.style.flexShrink = 0; titleRow.Add(head);
                if (!string.IsNullOrEmpty(branchName)) { var bch = GitWaypointTheme.Chip(branchName, GitWaypointTheme.UpToDate); bch.style.marginLeft = 4; bch.style.flexShrink = 0; titleRow.Add(bch); }
            }
            col.Add(titleRow);
            // meta: avatar + author · time · hash
            var meta = Row();
            meta.style.alignItems = Align.Center;
            meta.style.marginTop = 2;
            meta.Add(Avatar(entry.AuthorName, GitWaypointTheme.Accent, 16));
            meta.Add(new Label(entry.AuthorName + " · " + entry.PrettyTimeString) { style = { color = GitWaypointTheme.Subdued, fontSize = 10, marginLeft = 5, flexShrink = 1, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis, whiteSpace = WhiteSpace.NoWrap } });
            var hash = new Label(entry.ShortID); GitWaypointTheme.ApplyMono(hash); hash.style.fontSize = 10; hash.style.marginLeft = 8; hash.style.flexShrink = 0;
            meta.Add(hash);
            col.Add(meta);
            row.Add(col);
            return row;
        }

        void ShowCommitDetail(GitLogEntry entry)
        {
            historyDetail.Clear();
            historyDetail.style.display = DisplayStyle.Flex;
            historyDetail.Add(new Label(entry.summary) { style = { color = GitWaypointTheme.Text, fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, whiteSpace = WhiteSpace.Normal, marginBottom = 4 } });
            if (!string.IsNullOrEmpty(entry.description))
                historyDetail.Add(new Label(entry.description) { style = { color = GitWaypointTheme.Subdued, fontSize = 11, whiteSpace = WhiteSpace.Normal, marginBottom = 4 } });
            historyDetail.Add(new Label(entry.AuthorName + " · " + entry.PrettyTimeString + " · " + (entry.changes != null ? entry.changes.Count : 0) + " files") { style = { color = GitWaypointTheme.Subdued, fontSize = 10, marginBottom = 8 } });

            // Changed files, grouped by folder (the file tree the classic view had).
            if (entry.changes != null && entry.changes.Count > 0)
            {
                historyDetail.Add(SectionHeader("Files changed"));
                var files = new ScrollView { style = { maxHeight = 150, marginBottom = 8 } };
                var groups = entry.changes
                    .GroupBy(c => FolderOf(c.Path))
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var g in groups)
                {
                    if (!string.IsNullOrEmpty(g.Key))
                        files.Add(new Label("▸ " + g.Key) { style = { color = GitWaypointTheme.Subdued, fontSize = 11, paddingTop = 2, paddingBottom = 2 } });
                    foreach (var c in g.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
                        files.Add(BuildCommitFileRow(c));
                }
                historyDetail.Add(files);
            }

            var actions = Row();
            var copy = new Button(() => { EditorGUIUtility.systemCopyBuffer = entry.CommitID; SetStatus("Hash copied", Sev.Info); }) { text = "Copy hash" };
            StyleButton(copy, false); copy.style.marginRight = 6;
            actions.Add(copy);
            var revert = new Button(() =>
            {
                if (EditorUtility.DisplayDialog("Revert commit", "Create a new commit that undoes " + entry.ShortID + "?", "Revert", "Cancel"))
                    Repository?.Revert(entry.CommitID).FinallyInUI((s, e) =>
                    {
                        Repository?.Refresh(CacheType.GitLog); Repository?.Refresh(CacheType.GitStatus); needsRebuild = true;
                        if (!s) NotifyFailure("Revert", e); else SetStatus("Reverted " + entry.ShortID, Sev.Ok);
                    }).Start();
            }) { text = "Revert" };
            StyleButton(revert, false);
            actions.Add(revert);
            historyDetail.Add(actions);
        }

        static string FolderOf(string path)
        {
            try { var par = path.ToSPath().Parent; return par.IsInitialized && !par.IsEmpty ? par.ToString(SlashMode.Forward) : ""; }
            catch { return ""; }
        }

        VisualElement BuildCommitFileRow(GitStatusEntry c)
        {
            var sp = c.Path.ToSPath();
            var row = Row();
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 16; row.style.paddingTop = 1; row.style.paddingBottom = 1;
            var icon = AssetDatabase.GetCachedIcon(c.Path) as Texture;
            if (icon != null)
            {
                row.Add(new Image { image = icon, style = { width = 16, height = 16, marginRight = 4 } });
            }
            else
            {
                string letter; Color color; GitWaypointTheme.DiffBadge(c.Status, out letter, out color);
                var badge = GitWaypointTheme.BadgeSquare(letter, color);
                badge.style.width = 14; badge.style.height = 14; badge.style.marginRight = 4;
                row.Add(badge);
            }
            row.Add(new Label(sp.FileName) { style = { color = GitWaypointTheme.Text, fontSize = 11, whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } });
            return row;
        }

        // ---- Branches tab (native) -----------------------------------------------------------------

        VisualElement BuildBranchesTab()
        {
            var tab = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column, display = DisplayStyle.None } };

            var createRow = Row();
            createRow.style.alignItems = Align.Center;
            createRow.style.paddingLeft = 10; createRow.style.paddingRight = 10;
            createRow.style.paddingTop = 8; createRow.style.paddingBottom = 6;
            newBranchField = new TextField { style = { flexGrow = 1, marginRight = 6 } };
            SetPlaceholder(newBranchField, "New branch name…");
            Roomy(newBranchField, 24);
            createRow.Add(newBranchField);
            var createBtn = new Button(CreateBranch) { text = "Create" };
            StyleButton(createBtn, true);
            createRow.Add(createBtn);
            tab.Add(createRow);

            branchesList = new ScrollView { style = { flexGrow = 1, flexShrink = 1, minHeight = 0 } };
            tab.Add(branchesList);
            branchesEmpty = new Label("No branches.")
            {
                style = { flexGrow = 1, color = GitWaypointTheme.Subdued, unityTextAlign = TextAnchor.MiddleCenter, paddingTop = 24, paddingBottom = 24, display = DisplayStyle.None }
            };
            tab.Add(branchesEmpty);
            return tab;
        }

        void RefreshBranches()
        {
            if (branchesList == null) return;
            var repo = Repository;
            var local = repo != null ? (repo.LocalBranches ?? new GitBranch[0]) : new GitBranch[0];
            var remote = repo != null ? (repo.RemoteBranches ?? new GitBranch[0]) : new GitBranch[0];
            var current = repo != null ? repo.CurrentBranchName : null;

            branchesList.Clear();
            if (local.Length > 0)
            {
                branchesList.Add(SectionHeader("Local"));
                foreach (var b in local.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
                    branchesList.Add(BuildBranchRow(b, b.Name == current, false));
            }
            if (remote.Length > 0)
            {
                branchesList.Add(SectionHeader("Remote"));
                foreach (var b in remote.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
                    branchesList.Add(BuildBranchRow(b, false, true));
            }

            bool any = local.Length > 0 || remote.Length > 0;
            branchesEmpty.style.display = any ? DisplayStyle.None : DisplayStyle.Flex;
            branchesList.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;

            RefreshBranchAheadBehind(local);
        }

        // Refreshes the cached ahead/behind for each local tracking branch out-of-band from rendering.
        // Only flips needsRebuild when a value actually changed, so it converges (no rebuild storm) and the
        // chip never flickers: rows always render from the dict, this just keeps the dict current.
        void RefreshBranchAheadBehind(GitBranch[] local)
        {
            var mgr = Manager; if (mgr == null || mgr.GitClient == null) return;
            foreach (var b in local)
            {
                if (string.IsNullOrEmpty(b.Tracking) || b.Tracking == "[None]") continue;
                var name = b.Name; var tracking = b.Tracking;
                if (!aheadBehindInFlight.Add(name)) continue;
                mgr.GitClient.AheadBehindStatus(name, tracking).FinallyInUI((s, e, ab) =>
                {
                    aheadBehindInFlight.Remove(name);
                    if (!s) return;
                    if (!aheadBehindByBranch.TryGetValue(name, out var prev) || prev.Ahead != ab.Ahead || prev.Behind != ab.Behind)
                    {
                        aheadBehindByBranch[name] = ab;
                        needsRebuild = true;
                    }
                }).Start();
            }
        }

        VisualElement BuildBranchRow(GitBranch branch, bool isCurrent, bool isRemote)
        {
            var row = Row();
            row.style.alignItems = Align.Center;
            row.style.minHeight = 34; // keep rows the same height whether or not a Switch/Checkout button is shown
            row.style.paddingLeft = 10; row.style.paddingRight = 10;
            row.style.paddingTop = 4; row.style.paddingBottom = 4;
            Border(row, bottom: 1);

            var dot = new Label(isCurrent ? "●" : "○") { style = { color = isCurrent ? GitWaypointTheme.UpToDate : GitWaypointTheme.Subdued, fontSize = 11, marginRight = 6, width = 12 } };
            row.Add(dot);
            var name = new Label(branch.Name) { style = { flexGrow = 1, color = isCurrent ? GitWaypointTheme.Text : GitWaypointTheme.Text, fontSize = 12, unityFontStyleAndWeight = isCurrent ? FontStyle.Bold : FontStyle.Normal, whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } };
            GitWaypointTheme.ApplyMono(name); name.style.color = GitWaypointTheme.Text;
            row.Add(name);

            // Outdated state for any local branch that tracks a remote, even when it's not the one in checkout.
            // Lets you see at a glance how far behind/ahead each branch is without switching to it first.
            // Rendered synchronously from the cached value so a list rebuild doesn't drop and re-add the chip
            // (which looked like flashing); the value itself is refreshed in RefreshBranchAheadBehind().
            if (!isRemote && !string.IsNullOrEmpty(branch.Tracking) && branch.Tracking != "[None]"
                && aheadBehindByBranch.TryGetValue(branch.Name, out var ab) && (ab.Ahead > 0 || ab.Behind > 0))
            {
                // Two separate chips: ahead (to push, accent) and behind (to pull, outdated) are distinct
                // actions, so colour-code them rather than merging into one mixed-meaning chip.
                if (ab.Ahead > 0)
                {
                    var up = GitWaypointTheme.Chip("↑" + ab.Ahead, GitWaypointTheme.Accent);
                    up.tooltip = ab.Ahead + " to push to " + branch.Tracking;
                    up.style.marginLeft = 6; up.style.flexShrink = 0;
                    row.Add(up);
                }
                if (ab.Behind > 0)
                {
                    var down = GitWaypointTheme.Chip("↓" + ab.Behind, GitWaypointTheme.Outdated);
                    down.tooltip = ab.Behind + " to pull from " + branch.Tracking;
                    down.style.marginLeft = 4; down.style.flexShrink = 0;
                    row.Add(down);
                }
            }

            if (isCurrent)
            {
                var head = GitWaypointTheme.Chip("HEAD", GitWaypointTheme.Accent);
                head.style.marginLeft = 6; head.style.flexShrink = 0;
                row.Add(head);
            }

            if (!isCurrent)
            {
                var sw = new Button(() => { if (isRemote) CheckoutRemoteBranch(branch.Name); else SwitchBranch(branch.Name); }) { text = isRemote ? "Checkout" : "Switch" };
                StyleButton(sw, false); sw.style.flexShrink = 0;
                row.Add(sw);
            }
            if (!isCurrent && !isRemote)
            {
                row.AddManipulator(new ContextualMenuManipulator(evt =>
                    evt.menu.AppendAction("Delete branch", _ =>
                    {
                        if (EditorUtility.DisplayDialog("Delete branch", "Delete branch '" + branch.Name + "'?", "Delete", "Cancel"))
                            Repository?.DeleteBranch(branch.Name, false).FinallyInUI((s, e) =>
                            {
                                Repository?.Refresh(CacheType.Branches); needsRebuild = true;
                                if (!s) NotifyFailure("Delete branch", e);
                            }).Start();
                    })));
            }
            return row;
        }

        void SwitchBranch(string name)
        {
            var repo = Repository; if (repo == null) return;
            repo.SwitchBranch(name).FinallyInUI((s, e) =>
            {
                repo.Refresh(CacheType.Branches); repo.Refresh(CacheType.GitStatus); needsRebuild = true;
                if (s) { AssetDatabase.Refresh(); SetStatus("Switched to " + name, Sev.Ok); }
                else NotifyFailure("Switch branch", e);
            }).Start();
        }

        // Checking out a remote branch must create (or reuse) a local tracking branch and switch to it.
        // Running `git checkout origin/foo` directly would land in detached HEAD ([NoBranch]); instead we
        // strip the remote prefix and `git checkout foo`, which lets git DWIM the local tracking branch.
        void CheckoutRemoteBranch(string remoteBranch)
        {
            var slash = remoteBranch.IndexOf('/');
            var localName = slash >= 0 ? remoteBranch.Substring(slash + 1) : remoteBranch;
            SwitchBranch(localName);
        }

        void CreateBranch()
        {
            var repo = Repository; if (repo == null) return;
            var name = (newBranchField.value ?? "").Trim();
            if (string.IsNullOrEmpty(name)) return;
            repo.CreateBranch(name, repo.CurrentBranchName).FinallyInUI((s, e) =>
            {
                if (s) { newBranchField.value = ""; SetStatus("Created " + name, Sev.Ok); SwitchBranch(name); }
                else { repo.Refresh(CacheType.Branches); repo.Refresh(CacheType.GitStatus); needsRebuild = true; NotifyFailure("Create branch", e); }
            }).Start();
        }

        VisualElement SectionHeader(string text)
        {
            var l = new Label(text.ToUpperInvariant());
            l.style.color = GitWaypointTheme.Subdued; l.style.fontSize = 10;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.paddingLeft = 10; l.style.paddingTop = 8; l.style.paddingBottom = 4;
            return l;
        }

        // ---- Settings tab (native) -----------------------------------------------------------------

        VisualElement BuildSettingsTab()
        {
            var scroll = new ScrollView { style = { flexGrow = 1, display = DisplayStyle.None } };
            var root = new VisualElement { style = { paddingLeft = 12, paddingRight = 12, paddingTop = 4, paddingBottom = 14 } };
            scroll.Add(root);

            var repo = Repository;
            var env = Manager != null ? Manager.Environment : null;
            var user = env != null ? env.User : null;

            // Git user
            var userCard = SettingsCard("Git user", root);
            var nameField = settingsNameField = new TextField { value = user != null ? (user.Name ?? "") : "" }; Roomy(nameField, 24); nameField.style.width = 220;
            userCard.Add(SettingsRow("Name", nameField));
            var emailField = settingsEmailField = new TextField { value = user != null ? (user.Email ?? "") : "" }; Roomy(emailField, 24); emailField.style.width = 220;
            userCard.Add(SettingsRow("Email", emailField));
            var saveUser = new Button(() =>
            {
                var u = Manager != null && Manager.Environment != null ? Manager.Environment.User : null;
                if (u == null || string.IsNullOrEmpty(nameField.value) || string.IsNullOrEmpty(emailField.value)) return;
                try { identitySavePending = true; u.SetNameAndEmail(nameField.value, emailField.value); SetStatus("Saving identity…", Sev.Info); needsRebuild = true; }
                catch (Exception ex) { identitySavePending = false; NotifyFailure("Save user", ex); }
            }) { text = "Save" };
            StyleButton(saveUser, true); saveUser.style.marginTop = 4; saveUser.style.marginBottom = 6; saveUser.style.alignSelf = Align.FlexEnd;
            userCard.Add(saveUser);

            // Empty name/email get a red border and block Save — both are required for any commit.
            Action validateUser = () =>
            {
                bool nameOk = !string.IsNullOrWhiteSpace(nameField.value);
                bool emailOk = !string.IsNullOrWhiteSpace(emailField.value);
                SetFieldBorder(nameField, nameOk ? GitWaypointTheme.FieldBorder : GitWaypointTheme.Conflict);
                SetFieldBorder(emailField, emailOk ? GitWaypointTheme.FieldBorder : GitWaypointTheme.Conflict);
                saveUser.SetEnabled(nameOk && emailOk);
            };
            nameField.RegisterValueChangedCallback(_ => validateUser());
            emailField.RegisterValueChangedCallback(_ => validateUser());
            // Re-assert after Roomy's focus-out handler (which resets the border) so empty stays red.
            nameField.RegisterCallback<FocusOutEvent>(_ => validateUser());
            emailField.RegisterCallback<FocusOutEvent>(_ => validateUser());
            settingsValidateUser = validateUser;
            validateUser();

            // Repository
            var repoCard = SettingsCard("Repository", root);
            string remoteName = repo != null && repo.CurrentRemote.HasValue ? repo.CurrentRemote.Value.Name : "origin";
            var remoteField = new TextField { value = repo != null && repo.CurrentRemote.HasValue ? (repo.CurrentRemote.Value.Url ?? "") : "" };
            Roomy(remoteField, 24);
            repoCard.Add(FormField("Remote (" + remoteName + ")", remoteField));
            var saveRemote = new Button(() =>
            {
                var r = Repository; if (r == null || string.IsNullOrEmpty(remoteField.value)) return;
                r.SetupRemote(remoteName, remoteField.value).FinallyInUI((s, e) =>
                { if (s) SetStatus("Remote saved", Sev.Ok); else NotifyFailure("Save remote", e); }).Start();
            }) { text = "Save remote" };
            StyleButton(saveRemote, true); saveRemote.style.marginTop = 4; saveRemote.style.marginBottom = 6; saveRemote.style.alignSelf = Align.FlexEnd;
            repoCard.Add(saveRemote);

            // Read-only: how the server addresses you for locks. Resolved server-side (git lfs locks
            // --verify), so there's nothing to configure - just shown so you can confirm it's right.
            var who = ProjectWindowInterface.CurrentUsername;
            var lockIdField = new TextField { value = string.IsNullOrEmpty(who) ? "—" : who, isReadOnly = true };
            Roomy(lockIdField, 24);
            lockIdField.SetEnabled(false);
            repoCard.Add(FormField("Lock identity (read-only)", lockIdField));
            repoCard.Add(new Label("Who the server sees you as for file locks — used to tell your locks from teammates'. Learned automatically once you lock a file.")
            {
                style = { color = GitWaypointTheme.Subdued, fontSize = 10, whiteSpace = WhiteSpace.Normal, marginBottom = 6 }
            });

            var setupRow = Row();
            var gitattr = new Button(() =>
            {
                var r = Repository; if (r == null) return;
                if (r.LocalPath.Combine(".gitattributes").FileExists() &&
                    !EditorUtility.DisplayDialog("Overwrite .gitattributes?",
                        "This project already has a .gitattributes. Setting up replaces it with the default Git LFS rules for Unity — your own rules will be lost.\n\nContinue?",
                        "Overwrite", "Cancel"))
                    return;
                r.UpdateGitAttributes().FinallyInUI((s, e) =>
                { if (s) SetStatus(".gitattributes set up", Sev.Ok); else NotifyFailure("Set up .gitattributes", e); }).Start();
            }) { text = "Set up .gitattributes" };
            gitattr.tooltip = "Writes Git LFS tracking rules for common Unity binary assets into .gitattributes. Optional — skip it if you manage .gitattributes yourself, it may overwrite your rules.";
            StyleButton(gitattr, false); gitattr.style.marginRight = 6;
            var mergeBtn = new Button(() =>
            {
                var r = Repository; var e2 = Manager != null ? Manager.Environment : null; if (r == null || e2 == null) return;
                var exec = e2.UnityApplicationContents.ToSPath().Combine("Tools", "UnityYAMLMerge" + e2.ExecutableExtension);
                r.UpdateMergeSettings(exec).FinallyInUI((s, e) =>
                { if (s) SetStatus("Unity merge set up", Sev.Ok); else NotifyFailure("Set up Unity merge", e); }).Start();
            }) { text = "Set up Unity merge" };
            mergeBtn.tooltip = "Configures Git to use Unity's smart merge tool (UnityYAMLMerge) for scenes and prefabs, so merge conflicts in .unity/.prefab files resolve cleanly.";
            StyleButton(mergeBtn, false);
            repoCard.Add(new Label("Optional repository setup — hover each for details.")
                { style = { color = GitWaypointTheme.Subdued, fontSize = 10, marginTop = 6 } });
            setupRow.style.marginTop = 4; setupRow.style.marginBottom = 6;
            setupRow.Add(gitattr); setupRow.Add(mergeBtn);
            repoCard.Add(setupRow);

            // Git installation
            var giCard = SettingsCard("Git installation", root);
            gitPathField = new TextField { value = GitInstallPath(false) }; Roomy(gitPathField, 24);
            giCard.Add(PathHeader("Path to git", out gitVersionLabel));
            giCard.Add(PathRow(gitPathField, "Select git executable"));
            lfsPathField = new TextField { value = GitInstallPath(true) }; Roomy(lfsPathField, 24);
            giCard.Add(PathHeader("Path to git-lfs", out lfsVersionLabel));
            giCard.Add(PathRow(lfsPathField, "Select git-lfs executable"));
            RefreshGitVersions();
            var gitBtns = Row(); gitBtns.style.marginTop = 6; gitBtns.style.marginBottom = 6; gitBtns.style.justifyContent = Justify.FlexEnd;
            var findBtn = new Button(() => FindSystemGit(gitPathField, lfsPathField)) { text = "Find system git" }; StyleButton(findBtn, false); findBtn.style.marginRight = 6;
            var bundledBtn = new Button(UseBundledGit) { text = "Use bundled git" }; StyleButton(bundledBtn, false); bundledBtn.style.marginRight = 6;
            var applyBtn = new Button(() => ApplyGitPaths(gitPathField.value, lfsPathField.value)) { text = "Apply" }; StyleButton(applyBtn, true);
            gitBtns.Add(findBtn); gitBtns.Add(bundledBtn); gitBtns.Add(applyBtn);
            giCard.Add(gitBtns);

            // Sync
            var sync = SettingsCard("Sync", root);
            sync.Add(SettingsRow("Fetch automatically", "Fetch updates from the server in the background.",
                PlainToggle(ApplicationConfiguration.AutoFetchEnabled, v => { ApplicationConfiguration.AutoFetchEnabled = v; Manager.UserSettings.Set(Constants.AutoFetchEnabledKey, v); })));
            var interval = new SliderInt(1, 60) { value = Mathf.Clamp(ApplicationConfiguration.AutoFetchInterval, 1, 60), showInputField = true };
            interval.style.width = 150;
            interval.RegisterValueChangedCallback(e => { ApplicationConfiguration.AutoFetchInterval = e.newValue; Manager.UserSettings.Set(Constants.AutoFetchIntervalKey, e.newValue); });
            sync.Add(SettingsRow("Fetch interval", "Minutes between automatic fetches.", interval));
            sync.Add(SettingsRow("Block editing outdated files", "Stop you editing a file that has newer changes on the server.",
                PlainToggle(ApplicationConfiguration.BlockOutdatedEdit, v => { ApplicationConfiguration.BlockOutdatedEdit = v; Manager.UserSettings.Set(Constants.BlockOutdatedEditKey, v); })));
            sync.Add(SettingsRow("Block committing outdated files", "Stop you committing on top of newer server changes.",
                PlainToggle(ApplicationConfiguration.BlockOutdatedCommit, v => { ApplicationConfiguration.BlockOutdatedCommit = v; Manager.UserSettings.Set(Constants.BlockOutdatedCommitKey, v); })));

            // Automatic file locking
            var lk = SettingsCard("Automatic file locking", root);
            lk.Add(SettingsRow("Enable automatic locking", "Lock files as you work so teammates can't edit the same file.",
                PlainToggle(LfsLocksModificationProcessor.AutoLockEnabled, v => LfsLocksModificationProcessor.AutoLockEnabled = v)));
            lk.Add(SettingsRow("Lock on save", "Lock a file the first time you save changes to it.",
                PlainToggle(LfsLocksModificationProcessor.LockOnSave, v => LfsLocksModificationProcessor.LockOnSave = v)));
            var onOpen = new DropdownField(new List<string> { "Off", "Automatic", "Ask each time" }, Mathf.Clamp(LfsLocksModificationProcessor.OnOpenMode, 0, 2));
            onOpen.style.width = 130;
            onOpen.RegisterValueChangedCallback(_ => LfsLocksModificationProcessor.OnOpenMode = onOpen.index);
            lk.Add(SettingsRow("Lock on open", "What to do when you open a lockable file.", onOpen));
            lk.Add(SettingsRow("Release my locks on editor close", "Unlock your files automatically when you quit Unity.",
                PlainToggle(LfsLocksModificationProcessor.ReleaseOnClose, v => LfsLocksModificationProcessor.ReleaseOnClose = v)));
            lk.Add(SettingsRow("Block files locked by others", "Prevent opening/saving files a teammate has locked, and mark them read-only on disk.",
                PlainToggle(ApplicationConfiguration.BlockLockedByOthers, v =>
                {
                    ApplicationConfiguration.BlockLockedByOthers = v;
                    Manager.UserSettings.Set(Constants.BlockLockedByOthersKey, v);
                    LfsLocksModificationProcessor.OnEnforcementSettingChanged();
                })));
            lk.Add(new Label("Lockable files") { style = { color = GitWaypointTheme.Text, fontSize = 12, marginTop = 8 } });
            lk.Add(new Label("Which files are lockable is set by the `lockable` rules in your repository's .gitattributes - committed, so the whole team shares one policy. Edit .gitattributes to change it, or use \"Set up .gitattributes\" in the Repository section to install the recommended set.")
                { style = { color = GitWaypointTheme.Subdued, fontSize = 10, whiteSpace = WhiteSpace.Normal, marginBottom = 8 } });

            // Project window
            var pw = SettingsCard("Project window", root);
            pw.Add(SettingsRow("Show status icons", "Show Git status badges on assets in the Project window.",
                PlainToggle(ApplicationConfiguration.ProjectIconsEnabled, v => { ApplicationConfiguration.ProjectIconsEnabled = v; Manager.UserSettings.Set(Constants.ProjectIconsEnabledKey, v); })));

            // Scene hierarchy
            var hi = SettingsCard("Scene hierarchy", root);
            hi.Add(SettingsRow("Show status icons", "Show Git status badges on objects in the Hierarchy.",
                PlainToggle(ApplicationConfiguration.HierarchyIconsEnabled, v => { ApplicationConfiguration.HierarchyIconsEnabled = v; Manager.UserSettings.Set(Constants.HierarchyIconsEnabledKey, v); })));
            var align = new EnumField(ApplicationConfiguration.HierarchyIconsAlignment); align.style.width = 120;
            align.RegisterValueChangedCallback(e =>
            {
                var v = (ApplicationConfiguration.HierarchyIconAlignment)e.newValue;
                ApplicationConfiguration.HierarchyIconsAlignment = v; Manager.UserSettings.Set(Constants.HierarchyIconsAlignmentKey, (int)v);
            });
            hi.Add(SettingsRow("Align icons to", align));
            hi.Add(SettingsRow("Align to end of label", PlainToggle(ApplicationConfiguration.HierarchyIconsIndented, v =>
            { ApplicationConfiguration.HierarchyIconsIndented = v; Manager.UserSettings.Set(Constants.HierarchyIconsIndentedKey, v); })));
            var offR = new IntegerField { value = ApplicationConfiguration.HierarchyIconsOffsetRight, isDelayed = true }; offR.style.width = 56;
            offR.RegisterValueChangedCallback(e => { int v = Mathf.Clamp(e.newValue, 0, 200); ApplicationConfiguration.HierarchyIconsOffsetRight = v; Manager.UserSettings.Set(Constants.HierarchyIconsOffsetRightKey, v); if (v != e.newValue) offR.SetValueWithoutNotify(v); });
            hi.Add(SettingsRow("Offset right", offR));
            var offL = new IntegerField { value = ApplicationConfiguration.HierarchyIconsOffsetLeft, isDelayed = true }; offL.style.width = 56;
            offL.RegisterValueChangedCallback(e => { int v = Mathf.Clamp(e.newValue, -16, 16); ApplicationConfiguration.HierarchyIconsOffsetLeft = v; Manager.UserSettings.Set(Constants.HierarchyIconsOffsetLeftKey, v); if (v != e.newValue) offL.SetValueWithoutNotify(v); });
            hi.Add(SettingsRow("Offset left", offL));

            // Appearance
            var ap = SettingsCard("Appearance", root);
            ap.Add(SettingsRow("Compact rows", PlainToggle(CompactDensity, v => { CompactDensity = v; needsRebuild = true; })));

            // Advanced
            var adv = SettingsCard("Advanced", root);
            var webT = new IntegerField { value = ApplicationConfiguration.WebTimeout, isDelayed = true }; webT.style.width = 72;
            webT.RegisterValueChangedCallback(e => { ApplicationConfiguration.WebTimeout = e.newValue; Manager.UserSettings.Set(Constants.WebTimeoutKey, e.newValue); });
            adv.Add(SettingsRow("Web timeout (ms)", webT));
            var gitT = new IntegerField { value = ApplicationConfiguration.GitTimeout, isDelayed = true }; gitT.style.width = 72;
            gitT.RegisterValueChangedCallback(e => { ApplicationConfiguration.GitTimeout = e.newValue; Manager.UserSettings.Set(Constants.GitTimeoutKey, e.newValue); });
            adv.Add(SettingsRow("Git timeout (ms)", gitT));

            // Debug
            var dbg = SettingsCard("Debug", root);
            dbg.Add(SettingsRow("Enable trace logging", "Write detailed logs to the Console to help diagnose issues.",
                PlainToggle(LogHelper.TracingEnabled, v => { LogHelper.TracingEnabled = v; Manager.UserSettings.Set(Constants.TraceLoggingKey, v); })));

            return scroll;
        }

        VisualElement SettingsCard(string title, VisualElement parent)
        {
            var header = new Label(title.ToUpperInvariant());
            header.style.color = GitWaypointTheme.Subdued; header.style.fontSize = 10; header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.letterSpacing = 1; header.style.marginTop = 16; header.style.marginBottom = 6; header.style.marginLeft = 2;
            parent.Add(header);
            var card = new VisualElement();
            card.style.backgroundColor = GitWaypointTheme.Panel;
            GitWaypointTheme.Round(card, 8);
            card.style.paddingLeft = 12; card.style.paddingRight = 12;
            card.style.paddingTop = 4; card.style.paddingBottom = 4;
            card.style.borderTopWidth = card.style.borderBottomWidth = card.style.borderLeftWidth = card.style.borderRightWidth = 1;
            card.style.borderTopColor = card.style.borderBottomColor = card.style.borderLeftColor = card.style.borderRightColor = GitWaypointTheme.Border;
            parent.Add(card);
            return card;
        }

        // Vertical form field: caption above a full-width control. Use for text inputs — SettingsRow
        // puts the control beside a flex-grown label, which makes a text field fight for width.
        VisualElement FormField(string label, VisualElement field)
        {
            var col = new VisualElement { style = { flexDirection = FlexDirection.Column, marginTop = 4, marginBottom = 6 } };
            col.Add(new Label(label) { style = { color = GitWaypointTheme.Subdued, fontSize = 10, marginBottom = 2 } });
            col.Add(field);
            return col;
        }

        VisualElement SettingsRow(string label, VisualElement control) => SettingsRow(label, null, control);

        VisualElement SettingsRow(string label, string desc, VisualElement control)
        {
            var row = Row();
            row.style.alignItems = Align.Center;
            row.style.minHeight = 36;
            row.style.paddingTop = 5; row.style.paddingBottom = 5;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new Color(GitWaypointTheme.Border.r, GitWaypointTheme.Border.g, GitWaypointTheme.Border.b, 0.45f);
            var col = new VisualElement { style = { flexGrow = 1, flexShrink = 1, flexDirection = FlexDirection.Column, marginRight = 10 } };
            col.Add(new Label(label) { style = { color = GitWaypointTheme.Text, fontSize = 12, whiteSpace = WhiteSpace.Normal } });
            if (!string.IsNullOrEmpty(desc))
                col.Add(new Label(desc) { style = { color = GitWaypointTheme.Subdued, fontSize = 10, whiteSpace = WhiteSpace.Normal, marginTop = 1 } });
            row.Add(col);
            control.style.flexShrink = 0;
            row.Add(control);
            return row;
        }

        VisualElement SettingsRowText(string label, string value)
        {
            var v = new Label(value) { style = { color = GitWaypointTheme.Subdued, fontSize = 11, maxWidth = 210, whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } };
            GitWaypointTheme.ApplyMono(v);
            return SettingsRow(label, v);
        }

        // A pill switch (blue when on) instead of the default checkbox, to match the mockups.
        static VisualElement PlainToggle(bool value, Action<bool> onChange)
        {
            bool state = value;
            var track = new VisualElement { style = { width = 34, height = 18, flexShrink = 0 } };
            GitWaypointTheme.Round(track, 9);
            var knob = new VisualElement { style = { width = 14, height = 14, position = Position.Absolute, top = 2 } };
            GitWaypointTheme.Round(knob, 7);
            knob.style.backgroundColor = Color.white;
            track.Add(knob);
            Action render = () =>
            {
                track.style.backgroundColor = state ? GitWaypointTheme.Accent : GitWaypointTheme.Border;
                knob.style.left = state ? 18 : 2;
            };
            render();
            track.RegisterCallback<MouseDownEvent>(_ => { state = !state; render(); onChange(state); });
            return track;
        }

        string GitInstallPath(bool lfs)
        {
            try
            {
                var env = Manager != null ? Manager.Environment : null;
                if (env == null) return "—";
                var state = env.GitInstallationState;
                if (state == null) return "—";
                var s = "" + (lfs ? state.GitLfsExecutablePath : state.GitExecutablePath);
                return string.IsNullOrEmpty(s) ? "" : s;
            }
            catch { return ""; }
        }

        // Label line with the detected version on the right (e.g. "Path to git        v2.54.0").
        VisualElement PathHeader(string label, out Label versionLabel)
        {
            var row = Row(); row.style.alignItems = Align.Center; row.style.marginTop = 6;
            row.Add(new Label(label) { style = { color = GitWaypointTheme.Text, fontSize = 12 } });
            row.Add(Spacer());
            versionLabel = new Label("") { style = { color = GitWaypointTheme.Subdued, fontSize = 11, flexShrink = 0 } };
            GitWaypointTheme.ApplyMono(versionLabel);
            row.Add(versionLabel);
            return row;
        }

        string GitVersionStr(bool lfs)
        {
            try
            {
                var st = Manager != null && Manager.Environment != null ? Manager.Environment.GitInstallationState : null;
                if (st == null) return "";
                var s = "" + (lfs ? st.GitLfsVersion : st.GitVersion);
                return (string.IsNullOrEmpty(s) || s == "0" || s.StartsWith("0.0")) ? "" : s;
            }
            catch { return ""; }
        }

        void RefreshGitVersions()
        {
            if (gitVersionLabel != null) { var v = GitVersionStr(false); gitVersionLabel.text = string.IsNullOrEmpty(v) ? "" : "v" + v; }
            if (lfsVersionLabel != null) { var v = GitVersionStr(true); lfsVersionLabel.text = string.IsNullOrEmpty(v) ? "" : "v" + v; }
        }

        void RefreshGitPaths()
        {
            if (gitPathField != null) gitPathField.value = GitInstallPath(false);
            if (lfsPathField != null) lfsPathField.value = GitInstallPath(true);
            RefreshGitVersions();
        }

        VisualElement PathRow(TextField field, string browseTitle)
        {
            var row = Row();
            row.style.alignItems = Align.Center;
            row.style.marginTop = 2;
            // The value can be a long path that clips; surface the full text on hover.
            field.tooltip = field.value;
            field.RegisterValueChangedCallback(e => field.tooltip = e.newValue);
            // flexBasis:0 makes the field size from available space, not its (possibly very long) content,
            // so it never pushes the Browse button out of the panel.
            field.style.flexGrow = 1; field.style.flexShrink = 1; field.style.flexBasis = 0f;
            row.Add(field);
            var browse = new Button(() =>
            {
                var p = EditorUtility.OpenFilePanel(browseTitle, "", "");
                if (!string.IsNullOrEmpty(p)) field.value = p;
            }) { text = "Browse…" };
            StyleButton(browse, false); browse.style.marginLeft = 6; browse.style.flexShrink = 0;
            row.Add(browse);
            return row;
        }

        // Validate the given git/git-lfs paths and switch the plugin to use them.
        void ApplyGitPaths(string gitPath, string lfsPath)
        {
            var mgr = Manager; if (mgr == null || string.IsNullOrEmpty(gitPath)) return;
            SetStatus("Validating git…", Sev.Info);
            var newState = new GitInstaller.GitInstallationState();
            newState.GitExecutablePath = gitPath.ToSPath();
            if (!string.IsNullOrEmpty(lfsPath)) newState.GitLfsExecutablePath = lfsPath.ToSPath();
            var installer = new GitInstaller(mgr.Platform, newState);
            mgr.TaskManager.With(() => installer.RunSynchronously())
                .Then(state => { if (state.GitIsValid && state.GitLfsIsValid) { mgr.SetupGit(state); mgr.RestartRepository(); } return state; })
                .FinallyInUI((success, ex, state) =>
                {
                    if (success && state != null && state.GitIsValid && state.GitLfsIsValid) { SetStatus("Git path applied", Sev.Ok); RefreshGitPaths(); }
                    else { SetStatus("Invalid git path", Sev.Error); Toast("Invalid git path", ex != null ? ex.Message : "git or git-lfs not valid", Sev.Error); }
                }).Start();
        }

        void FindSystemGit(TextField gitField, TextField lfsField)
        {
            var mgr = Manager; if (mgr == null) return;
            SetStatus("Finding system git…", Sev.Info);
            mgr.TaskManager.With(() =>
            {
                var inst = new GitInstaller(mgr.Platform);
                return inst.FindSystemGit(new GitInstaller.GitInstallationState());
            }).FinallyInUI((success, ex, state) =>
            {
                if (success && state != null && state.GitIsValid)
                {
                    gitField.value = "" + state.GitExecutablePath;
                    lfsField.value = "" + state.GitLfsExecutablePath;
                    ApplyGitPaths(gitField.value, lfsField.value);
                }
                else { SetStatus("System git not found", Sev.Warn); Toast("System git not found", "Install git, or set the path manually.", Sev.Warn); }
            }).Start();
        }

        void UseBundledGit()
        {
            var mgr = Manager; var env = mgr != null ? mgr.Environment : null;
            if (mgr == null || env == null) return;
            SetStatus("Setting up bundled git…", Sev.Info);
            mgr.TaskManager.With(() =>
            {
                // Reuse the already-installed bundled git (no needless re-download); the installer only
                // fetches when it's missing or invalid.
                var state = env.GitDefaultInstallation.GetDefaults();
                var inst = new GitInstaller(mgr.Platform, state);
                state = inst.RunSynchronously();
                if (state.GitIsValid && state.GitLfsIsValid) { mgr.SetupGit(state); mgr.RestartRepository(); }
                return state;
            }).FinallyInUI((success, ex, state) =>
            {
                if (success && state != null && state.GitIsValid && state.GitLfsIsValid)
                { SetStatus("Using bundled git " + state.GitVersion, Sev.Ok); RefreshGitPaths(); }
                else
                {
                    var why = ex != null ? ex.Message : (state != null && !state.GitLfsIsValid ? "git-lfs missing from the bundle" : "setup failed");
                    SetStatus("Bundled git setup failed", Sev.Error); Toast("Bundled git", why, Sev.Error);
                }
            }).Start();
        }

        static bool CompactDensity
        {
            get => EditorPrefs.GetBool("wayGit.compact", false);
            set => EditorPrefs.SetBool("wayGit.compact", value);
        }

        // ---- Operations ----------------------------------------------------------------------------

        void DoRefresh()
        {
            var repo = Repository; if (repo == null) return;
            repo.Refresh(CacheType.GitStatus);
            repo.Refresh(CacheType.GitLocks);
            LfsLocksModificationProcessor.RefreshOutdated();
            RefreshActive();
            SetStatus("Refreshed", Sev.Ok);
        }

        void DoFetch()
        {
            var repo = Repository; if (repo == null || runningOp != null) return;
            BeginOp("Fetching");
            repo.Fetch().FinallyInUI((s, e) =>
            {
                LfsLocksModificationProcessor.RefreshOutdated();
                if (!s) { EndOp(false, e, "Fetch", null); return; }
                runningOp = null; needsRebuild = true;
                int behind = repo.CurrentBehind;
                if (behind > 0)
                {
                    SetStatus("Fetched — " + behind + " to pull", Sev.Ok);
                    Toast("Fetched", behind + " new commit" + (behind == 1 ? "" : "s") + " on " + RemoteShort(repo), Sev.Ok);
                }
                else SetStatus("Up to date", Sev.Ok);
            }).Start();
        }

        static string RemoteShort(IRepository repo) => repo.CurrentRemote.HasValue ? repo.CurrentRemote.Value.Name : "remote";

        void DoPull()
        {
            var repo = Repository; if (repo == null || runningOp != null) return;
            if (repo.CurrentBehind == 0)
            {
                SetStatus("Already up to date — nothing to pull", Sev.Info);
                return;
            }
            var choice = EditorUtility.DisplayDialogComplex("Pull", "Bring in changes from the remote.", "Merge", "Cancel", "Rebase");
            if (choice == 1) return;
            BeginOp("Pulling");
            repo.Pull(choice == 2).FinallyInUI((s, e) =>
            {
                LfsLocksModificationProcessor.RefreshOutdated();
                EndOp(s, e, "Pull", "Pulled from remote");
            }).Start();
        }

        void DoPush()
        {
            var repo = Repository; if (repo == null || runningOp != null) return;
            if (repo.CurrentAhead == 0)
            {
                SetStatus("Nothing to push", Sev.Info);
                return;
            }
            BeginOp("Pushing");
            repo.Push().FinallyInUI((s, e) =>
            {
                if (s) LfsLocksModificationProcessor.ReleaseLocksAfterPush();
                EndOp(s, e, "Push", "Pushed to remote", toastOk: true);
            }).Start();
        }

        void Commit(bool push)
        {
            var repo = Repository; if (repo == null || isBusy) return;
            var files = checkedPaths.ToList();
            if (files.Count == 0 || string.IsNullOrEmpty(commitMessage.value)) return;

            // Don't let a commit land on top of files that have newer changes on the server.
            if (ApplicationConfiguration.BlockOutdatedCommit)
            {
                var outdated = files.Where(IsOutdated).ToList();
                if (outdated.Count > 0)
                {
                    SetStatus("Commit blocked: " + outdated.Count + " file(s) out of date — update from server first", Sev.Warn);
                    Toast("Outdated files", "These have newer changes on the server. Pull first:\n" + string.Join("\n", outdated.Take(5)) + (outdated.Count > 5 ? "\n…" : ""), Sev.Warn);
                    return;
                }
            }

            isBusy = true;
            BeginOp("Committing");
            UpdateCommitEnabled();

            var body = commitDescription.value ?? "";
            var allChanges = (repo.CurrentChanges ?? new List<GitStatusEntry>()).Where(x => x.Status != GitFileStatus.Ignored).ToList();
            ITask task;
            if (files.Count == allChanges.Count)
            {
                task = repo.CommitAllFiles(commitMessage.value, body);
            }
            else
            {
                ITask commit = repo.CommitFiles(files, commitMessage.value, body);
                var stagedNotChecked = allChanges.Where(x => x.Staged).Select(x => x.Path).Except(files).ToList();
                task = stagedNotChecked.Count > 0 ? Manager.GitClient.Remove(stagedNotChecked).Then(commit) : commit;
            }

            task.FinallyInUI((success, ex) =>
            {
                if (success)
                {
                    commitMessage.value = "";
                    commitDescription.value = "";
                    repo.Refresh(CacheType.GitStatus);
                    repo.Refresh(CacheType.GitLocks);
                    if (push)
                    {
                        runningOp = "Pushing";
                        repo.Push().FinallyInUI((ps, pe) =>
                        {
                            if (ps) LfsLocksModificationProcessor.ReleaseLocksAfterPush();
                            isBusy = false;
                            EndOp(ps, pe, "Push", "Committed & pushed", toastOk: true);
                        }).Start();
                        return;
                    }
                    isBusy = false;
                    EndOp(true, null, "Commit", "Committed");
                    return;
                }
                isBusy = false;
                EndOp(false, ex, "Commit", null);
            }).Start();
        }

        void BeginOp(string label, bool syncState = true)
        {
            runningOp = label;
            opStartTime = EditorApplication.timeSinceStartup;
            fetchButton.SetEnabled(false); pullButton.SetEnabled(false); pushButton.SetEnabled(false);
            if (discardAllButton != null) discardAllButton.SetEnabled(false);
            // Remote operations use the sync pill; local-only cleanup shows its own label there.
            syncLabel.text = syncState ? "Syncing" : label;
            syncLabel.style.color = GitWaypointTheme.Accent;
            SetStatus(label + "…", Sev.Info);
        }

        void EndOp(bool success, Exception ex, string opName, string okMessage, bool toastOk = false)
        {
            runningOp = null;
            needsRebuild = true;
            if (success)
            {
                if (!string.IsNullOrEmpty(okMessage)) SetStatus(okMessage, Sev.Ok);
                if (toastOk && !string.IsNullOrEmpty(okMessage)) Toast(okMessage, null, Sev.Ok);
            }
            else NotifyFailure(opName, ex);
        }

        void NotifyFailure(string op, Exception ex)
        {
            SetStatus(op + " failed", Sev.Error);
            Toast(op + " failed", ex != null ? ex.Message : "see Console", Sev.Error);
            if (ex != null) Debug.LogWarning("Git Waypoint: " + op + " failed: " + ex.Message);
        }

        // The persistent bottom strip: every action lands here, discreet and always visible.
        void SetStatus(string message, Sev sev)
        {
            if (statusMessage == null) return;
            statusStamp = EditorApplication.timeSinceStartup;
            statusMessage.text = message;
            statusMessage.style.color = sev == Sev.Info ? GitWaypointTheme.Subdued : GitWaypointTheme.Text;
            statusIcon.text = Glyph(sev);
            statusIcon.style.color = SevColor(sev);
            statusTime.text = "now";
        }

        static string AgoShort(double seconds)
        {
            if (seconds < 5) return "now";
            if (seconds < 60) return (int)seconds + "s ago";
            if (seconds < 3600) return (int)(seconds / 60) + "m ago";
            return (int)(seconds / 3600) + "h ago";
        }

        // A floating toast for errors and important outcomes. Auto-dismisses, stacks, dismissable.
        void Toast(string title, string subtitle, Sev sev)
        {
            if (toastContainer == null) return;
            var t = new VisualElement();
            t.style.flexDirection = FlexDirection.Row;
            t.style.minWidth = 200; t.style.maxWidth = 320; t.style.marginTop = 6;
            t.style.backgroundColor = GitWaypointTheme.Elevated;
            GitWaypointTheme.Round(t, 6);
            t.style.paddingTop = 8; t.style.paddingBottom = 8; t.style.paddingLeft = 10; t.style.paddingRight = 8;
            t.style.borderLeftWidth = 3; t.style.borderLeftColor = SevColor(sev);
            t.Add(new Label(Glyph(sev)) { style = { color = SevColor(sev), fontSize = 12, marginRight = 8, flexShrink = 0 } });
            var col = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column } };
            col.Add(new Label(title) { style = { color = GitWaypointTheme.Text, fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold } });
            if (!string.IsNullOrEmpty(subtitle))
                col.Add(new Label(subtitle) { style = { color = GitWaypointTheme.Subdued, fontSize = 10, whiteSpace = WhiteSpace.Normal, marginTop = 1 } });
            t.Add(col);
            var close = new Label("✕") { style = { color = GitWaypointTheme.Subdued, fontSize = 11, marginLeft = 8, flexShrink = 0 } };
            close.RegisterCallback<MouseDownEvent>(_ => t.RemoveFromHierarchy());
            t.Add(close);
            toastContainer.Add(t);
            while (toastContainer.childCount > 4) toastContainer.RemoveAt(0);
            t.schedule.Execute(() => { if (t.parent != null) t.RemoveFromHierarchy(); }).StartingIn(4500);
        }

        static string Glyph(Sev sev)
        {
            switch (sev) { case Sev.Ok: return "✓"; case Sev.Warn: return "⚠"; case Sev.Error: return "✕"; default: return "•"; }
        }

        static Color SevColor(Sev sev)
        {
            switch (sev) { case Sev.Ok: return GitWaypointTheme.UpToDate; case Sev.Warn: return GitWaypointTheme.Outdated; case Sev.Error: return GitWaypointTheme.Conflict; default: return GitWaypointTheme.Subdued; }
        }

        // ---- Per-file context actions --------------------------------------------------------------

        // Delegate to ProjectWindowInterface so the optimistic "Locking…/Releasing…" state is the SAME one
        // the Project window overlay uses - a lock acquired anywhere (here, or by editing a file) shows up
        // consistently, and the row stays in its pending state until the operation truly completes.
        void RequestLock(SPath path)
        {
            ProjectWindowInterface.RequestLock(path.ToString(), err => NotifyFailure("Lock", new Exception(err)));
            needsRebuild = true;
        }

        void ForceUnlockConfirm(GitLock lck)
        {
            if (EditorUtility.DisplayDialog("Force unlock",
                "Force-unlock " + lck.Owner.Name + "'s lock on " + lck.Path.FileName + "?\nThey may lose work if they're still editing it.",
                "Force unlock", "Cancel"))
                ReleaseLock(lck, true);
        }

        void DiscardChanges(GitStatusEntry entry)
        {
            var repo = Repository; if (repo == null) return;
            if (!EditorUtility.DisplayDialog("Discard changes",
                "Discard your changes to " + entry.Path + "?\nThis cannot be undone.", "Discard", "Cancel"))
                return;
            repo.DiscardChanges(new[] { entry }).FinallyInUI((s, e) =>
            {
                AssetDatabase.Refresh();
                // Discarding abandons your edits, so drop your own lock on the file.
                var me = ProjectWindowInterface.CurrentUsername;
                var cur = repo.CurrentLocks;
                if (cur != null && !string.IsNullOrEmpty(me))
                {
                    var p = entry.Path.ToSPath();
                    foreach (var l in cur)
                        if (l.Path == p && l.Owner.Name == me) { repo.ReleaseLock(p, l.ID, false).Start(); break; }
                }
                repo.Refresh(CacheType.GitStatus); repo.Refresh(CacheType.GitLocks); needsRebuild = true;
            }).Start();
        }

        void DiscardAllChanges()
        {
            var repo = Repository; if (repo == null || runningOp != null || isBusy) return;
            var dirtyEntries = (repo.CurrentChanges ?? new List<GitStatusEntry>())
                .Where(x => x.Status != GitFileStatus.Ignored)
                .ToList();
            if (dirtyEntries.Count == 0)
            {
                SetStatus("No changes to discard", Sev.Info);
                return;
            }

            var message = dirtyEntries.Count == 1
                ? "Remove the uncommitted change and any new files?\nThis cannot be undone."
                : "Remove all " + dirtyEntries.Count + " uncommitted changes and any new files?\nThis cannot be undone.";
            if (!EditorUtility.DisplayDialog("Discard all changes", message, "Discard all", "Cancel"))
                return;

            var me = ProjectWindowInterface.CurrentUsername;
            var dirtyPaths = new HashSet<SPath>(dirtyEntries.Select(e => e.Path.ToSPath()));
            var ownLocksToRelease = string.IsNullOrEmpty(me) || repo.CurrentLocks == null
                ? new List<GitLock>()
                : repo.CurrentLocks.Where(l => l.Owner.Name == me && dirtyPaths.Contains(l.Path)).ToList();

            isBusy = true;
            BeginOp("Discarding changes", false);
            UpdateCommitEnabled();
            repo.DiscardAllChanges().FinallyInUI((s, e) =>
            {
                AssetDatabase.Refresh();
                if (s)
                {
                    foreach (var lck in ownLocksToRelease)
                        repo.ReleaseLock(lck.Path, lck.ID, false).Start();
                    checkedPaths.Clear();
                    knownPaths.Clear();
                }
                repo.Refresh(CacheType.GitStatus);
                repo.Refresh(CacheType.GitLocks);
                LfsLocksModificationProcessor.RefreshOutdated();
                isBusy = false;
                EndOp(s, e, "Discard all", s ? "All changes discarded" : null);
            }).Start();
        }

        static void PingAsset(string path)
        {
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
        }

        void ShowDiff(GitStatusEntry entry)
        {
            CalcFileDiff(entry).FinallyInUI((s, ex, lr) =>
            {
                if (!s || lr == null) { Debug.LogWarning("Show Diff: couldn't prepare the diff: " + (ex != null ? ex.Message : "unknown")); return; }
                if (TryOpenDiffExternally(lr[0], lr[1])) return;
                EditorUtility.InvokeDiffTool(lr[0].IsInitialized ? lr[0].FileName : null, lr[0].IsInitialized ? lr[0].MakeAbsolute().ToString() : null,
                    lr[1].IsInitialized ? lr[1].FileName : null, lr[1].IsInitialized ? lr[1].MakeAbsolute().ToString() : null, null, null);
            }).Start();
        }

        ITask<SPath[]> CalcFileDiff(GitStatusEntry entry)
        {
            var rightFile = entry.Path.ToSPath();
            var tmpDir = Manager.Environment.UnityProjectPath.ToSPath().Combine("Temp", "ghu-diffs").EnsureDirectoryExists();
            var leftFile = tmpDir.Combine(rightFile.FileNameWithoutExtension + "_" + Repository.CurrentHead + rightFile.ExtensionWithDot);
            // Go through the git client (same process env/auth as every other op) rather than building a raw task here.
            return Manager.GitClient.GetFileContentAtHead(rightFile.ToString(SlashMode.Forward))
                .Catch(_ => true)
                .Then((success, ex, txt) =>
                {
                    if (success && rightFile.FileExists())
                    {
                        leftFile.WriteAllText(txt);
                        return new SPath[] { leftFile, rightFile };
                    }
                    var leftFolder = tmpDir.Combine("left", leftFile.FileName).EnsureDirectoryExists();
                    var rightFolder = tmpDir.Combine("right", leftFile.FileName).EnsureDirectoryExists();
                    if (!rightFile.FileExists()) leftFolder.Combine(rightFile).WriteAllText(txt);
                    if (!success) rightFolder.Combine(rightFile).WriteAllText(rightFile.ReadAllText());
                    return new SPath[] { leftFolder.Combine(rightFile), rightFolder.Combine(rightFile) };
                });
        }

        // Open the two versions in a diff tool we locate ourselves (macOS FileMerge, else VS Code), so it
        // works without configuring Unity's external diff tool. Returns false to fall back.
        static bool TryOpenDiffExternally(SPath left, SPath right)
        {
            if (!left.IsInitialized || !right.IsInitialized) return false;
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

        static bool TryStartDiff(string exe, string args)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false });
                return true;
            }
            catch (Exception e) { Debug.LogWarning("Show Diff: failed to launch " + exe + ": " + e.Message); return false; }
        }

        // ---- Helpers -------------------------------------------------------------------------------

        void SetAllChecked(bool value)
        {
            checkedPaths.Clear();
            if (value) foreach (var p in knownPaths) checkedPaths.Add(p);
            RefreshChanges();
        }

        // Blocking outdated commits is a core safety: never commit over files that are newer on the server.
        bool HasOutdatedChecked() => ApplicationConfiguration.BlockOutdatedCommit && checkedPaths.Any(IsOutdated);

        void UpdateCommitEnabled()
        {
            bool can = !isBusy && Repository != null && checkedPaths.Count > 0 && !string.IsNullOrEmpty(commitMessage?.value)
                       && !IdentityMissing() && !HasOutdatedChecked();
            commitButton.SetEnabled(can);
            commitPushButton.SetEnabled(can && Repository != null && Repository.CurrentRemote.HasValue);
            commitButton.text = Repository != null && !string.IsNullOrEmpty(Repository.CurrentBranchName)
                ? "Commit to " + Repository.CurrentBranchName : "Commit";
        }

        void SetRemoteButtonsEnabled(bool enabled)
        {
            fetchButton.SetEnabled(enabled);
            pullButton.SetEnabled(enabled);
            pushButton.SetEnabled(enabled);
        }

        static bool IsOutdated(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            return !string.IsNullOrEmpty(guid) && ProjectWindowInterface.IsOutdated(guid);
        }

        static Button TbButton(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.marginRight = 6; b.style.marginLeft = 0; b.style.marginTop = 0; b.style.marginBottom = 0;
            StyleButton(b, false);
            return b;
        }

        static void StyleButton(Button b, bool primary)
        {
            var bg = primary ? GitWaypointTheme.Accent : GitWaypointTheme.Elevated;
            var hover = primary ? Lighten(GitWaypointTheme.Accent, 0.10f) : GitWaypointTheme.Hover;
            b.style.height = 26;
            b.style.paddingLeft = 14; b.style.paddingRight = 14;
            b.style.paddingTop = 0; b.style.paddingBottom = 0;
            b.style.color = primary ? Color.white : GitWaypointTheme.Text;
            b.style.backgroundColor = bg;
            b.style.borderTopWidth = b.style.borderBottomWidth = b.style.borderLeftWidth = b.style.borderRightWidth = primary ? 0 : 1;
            b.style.borderTopColor = b.style.borderBottomColor = b.style.borderLeftColor = b.style.borderRightColor = GitWaypointTheme.Border;
            GitWaypointTheme.Round(b, 7);
            b.RegisterCallback<MouseEnterEvent>(_ => { if (b.enabledSelf) b.style.backgroundColor = hover; });
            b.RegisterCallback<MouseLeaveEvent>(_ => b.style.backgroundColor = bg);
        }

        static void StyleDangerButton(Button b)
        {
            var danger = GitWaypointTheme.Conflict;
            var bg = new Color(danger.r, danger.g, danger.b, 0.12f);
            var hover = new Color(danger.r, danger.g, danger.b, 0.20f);
            var border = new Color(danger.r, danger.g, danger.b, 0.55f);
            b.style.height = 26;
            b.style.paddingLeft = 14; b.style.paddingRight = 14;
            b.style.paddingTop = 0; b.style.paddingBottom = 0;
            b.style.color = danger;
            b.style.backgroundColor = bg;
            b.style.borderTopWidth = b.style.borderBottomWidth = b.style.borderLeftWidth = b.style.borderRightWidth = 1;
            b.style.borderTopColor = b.style.borderBottomColor = b.style.borderLeftColor = b.style.borderRightColor = border;
            GitWaypointTheme.Round(b, 6);
            b.RegisterCallback<MouseEnterEvent>(_ => { if (b.enabledSelf) b.style.backgroundColor = hover; });
            b.RegisterCallback<MouseLeaveEvent>(_ => b.style.backgroundColor = bg);
        }

        static Color Lighten(Color c, float amt) => new Color(Mathf.Min(1, c.r + amt), Mathf.Min(1, c.g + amt), Mathf.Min(1, c.b + amt), c.a);
        static VisualElement Row() { var v = new VisualElement(); v.style.flexDirection = FlexDirection.Row; return v; }
        static VisualElement Spacer() { var v = new VisualElement(); v.style.flexGrow = 1; return v; }

        static void Border(VisualElement e, int bottom = 0, int top = 0)
        {
            if (bottom > 0) { e.style.borderBottomWidth = bottom; e.style.borderBottomColor = GitWaypointTheme.Border; }
            if (top > 0) { e.style.borderTopWidth = top; e.style.borderTopColor = GitWaypointTheme.Border; }
        }

        static void SetPlaceholder(TextField field, string text)
        {
            field.textEdition.placeholder = text;
            field.textEdition.hidePlaceholderOnFocus = true;
        }

        // Consistent text-field styling. Width is left to the container: in a column (forms) the field
        // stretches; in a row, pair it with flexGrow on the call site. minWidth:0 lets it shrink so a
        // long value clips instead of overflowing the panel.
        static void Roomy(TextField field, float minHeight)
        {
            field.style.minHeight = minHeight;
            field.style.minWidth = 0;

            StyleFieldInput(field, minHeight);
            // The input child may not exist until the field is attached to a panel (notably multiline),
            // so (re)apply on attach to make styling reliable.
            field.RegisterCallback<AttachToPanelEvent>(_ => StyleFieldInput(field, minHeight));
            // Focus affordance: brighten the border while editing.
            field.RegisterCallback<FocusInEvent>(_ => SetFieldBorder(field, GitWaypointTheme.Accent));
            field.RegisterCallback<FocusOutEvent>(_ => SetFieldBorder(field, GitWaypointTheme.FieldBorder));
        }

        static void StyleFieldInput(TextField field, float minHeight)
        {
            var input = field.Q(className: "unity-base-text-field__input");
            if (input == null) return;
            input.style.minHeight = minHeight;
            input.style.minWidth = 0; // the input is itself a flex child; let it shrink so it never overflows
            input.style.paddingLeft = 8; input.style.paddingRight = 8;
            input.style.paddingTop = 4; input.style.paddingBottom = 4;
            input.style.backgroundColor = GitWaypointTheme.Field;
            input.style.color = GitWaypointTheme.Text;
            // Clip long single-line values (e.g. a deep git path); multiline must stay scrollable.
            if (!field.multiline) input.style.overflow = Overflow.Hidden;
            GitWaypointTheme.Round(input, 4);
            SetFieldBorder(field, GitWaypointTheme.FieldBorder);
        }

        static void SetFieldBorder(TextField field, Color c)
        {
            var input = field.Q(className: "unity-base-text-field__input");
            if (input == null) return;
            input.style.borderTopWidth = input.style.borderBottomWidth =
                input.style.borderLeftWidth = input.style.borderRightWidth = 1;
            input.style.borderTopColor = input.style.borderBottomColor =
                input.style.borderLeftColor = input.style.borderRightColor = c;
        }

        // ---- Repository events ---------------------------------------------------------------------

        void Subscribe()
        {
            var repo = Repository;
            if (repo == null || subscribed) return;
            repo.StatusEntriesChanged += OnCacheChanged;
            repo.CurrentBranchChanged += OnCacheChanged;
            repo.TrackingStatusChanged += OnCacheChanged;
            repo.LocksChanged += OnCacheChanged;
            repo.LogChanged += OnCacheChanged;
            repo.LocalAndRemoteBranchListChanged += OnCacheChanged;
            subscribed = true;
        }

        void Unsubscribe()
        {
            var repo = Repository;
            if (repo == null || !subscribed) return;
            repo.StatusEntriesChanged -= OnCacheChanged;
            repo.CurrentBranchChanged -= OnCacheChanged;
            repo.TrackingStatusChanged -= OnCacheChanged;
            repo.LocksChanged -= OnCacheChanged;
            repo.LogChanged -= OnCacheChanged;
            repo.LocalAndRemoteBranchListChanged -= OnCacheChanged;
            subscribed = false;
        }

        void OnCacheChanged(CacheUpdateEvent _) => needsRebuild = true;
    }
}
