namespace Unity.VersionControl.Git
{
    public static class ApplicationConfiguration
    {
        public const int DefaultWebTimeout = 100*1000;
        public const int DefaultGitTimeout = 5000;
        public static int WebTimeout { get; set; } = DefaultWebTimeout;
        public static int GitTimeout { get; set; } = DefaultGitTimeout;
        public static bool HierarchyIconsEnabled { get; set; } = false;
        public static bool HierarchyIconsIndented { get; set; } = false;
        public static int HierarchyIconsOffsetRight { get; set; } = 0;
        public static int HierarchyIconsOffsetLeft { get; set; } = 0;
        public static HierarchyIconAlignment HierarchyIconsAlignment { get; set; } = HierarchyIconAlignment.Right;
        public static bool ProjectIconsEnabled { get; set; } = true;
        public const int DefaultAutoFetchInterval = 5;
        public static bool AutoFetchEnabled { get; set; } = true;
        public static int AutoFetchInterval { get; set; } = DefaultAutoFetchInterval; // minutes
        public static bool BlockOutdatedEdit { get; set; } = false;
        public static bool BlockOutdatedCommit { get; set; } = false;

        public static void Initialize(ISettings settings)
        {
            WebTimeout = settings.Get(Constants.WebTimeoutKey, WebTimeout);
            GitTimeout = settings.Get(Constants.GitTimeoutKey, GitTimeout);
            HierarchyIconsEnabled = settings.Get(Constants.HierarchyIconsEnabledKey, HierarchyIconsEnabled);
            HierarchyIconsIndented = settings.Get(Constants.HierarchyIconsIndentedKey, HierarchyIconsIndented);
            HierarchyIconsOffsetRight = settings.Get(Constants.HierarchyIconsOffsetRightKey, HierarchyIconsOffsetRight);
            HierarchyIconsOffsetLeft = settings.Get(Constants.HierarchyIconsOffsetLeftKey, HierarchyIconsOffsetLeft);
            HierarchyIconsAlignment = (HierarchyIconAlignment) settings.Get(Constants.HierarchyIconsAlignmentKey, (int)HierarchyIconsAlignment);
            ProjectIconsEnabled = settings.Get(Constants.ProjectIconsEnabledKey, ProjectIconsEnabled);
            AutoFetchEnabled = settings.Get(Constants.AutoFetchEnabledKey, AutoFetchEnabled);
            AutoFetchInterval = settings.Get(Constants.AutoFetchIntervalKey, AutoFetchInterval);
            BlockOutdatedEdit = settings.Get(Constants.BlockOutdatedEditKey, BlockOutdatedEdit);
            BlockOutdatedCommit = settings.Get(Constants.BlockOutdatedCommitKey, BlockOutdatedCommit);
        }

        public enum HierarchyIconAlignment
        {
            Right = 0,
            Left = 1,
        }
    }
}
