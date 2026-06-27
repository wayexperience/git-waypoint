using System.Threading;
using Unity.Editor.Tasks;

namespace Unity.VersionControl.Git.Tasks
{
    public class GitLogTask : GitProcessListTask<GitLogEntry>
    {
        private const string TaskName = "git log";
        private const string baseArguments = @"-c i18n.logoutputencoding=utf8 -c core.quotepath=false log --pretty=format:""%H%n%P%n%aN%n%aE%n%aI%n%cN%n%cE%n%cI%n%B---GHUBODYEND---"" --name-status";
        private readonly string arguments;

        public GitLogTask(IPlatform platform,
            IGitObjectFactory gitObjectFactory,
            int numberOfCommits,
            CancellationToken token = default)
            : this(platform, gitObjectFactory, null, numberOfCommits, token: token)
        {}

        public GitLogTask(IPlatform platform,
            IGitObjectFactory gitObjectFactory,
            string file,
            CancellationToken token = default)
            : this(platform, gitObjectFactory, file, 0, token: token)
        {}

        public GitLogTask(IPlatform platform,
            IGitObjectFactory gitObjectFactory,
            string file = null, int numberOfCommits = 0, string revisionRange = null,
            CancellationToken token = default)
            : base(platform, null, outputProcessor: new LogEntryOutputProcessor(gitObjectFactory), token: token)
        {
            Name = TaskName;
            arguments = baseArguments;
            if (numberOfCommits > 0)
                arguments += " -n " + numberOfCommits;

            // A revision range (e.g. "HEAD..@{u}") goes after the log options and before any pathspec.
            if (!string.IsNullOrEmpty(revisionRange))
                arguments += " " + revisionRange;

            if (file != null)
            {
                arguments += " -- ";
                arguments += " \"" + file + "\"";
            }
        }
        public override string ProcessArguments => arguments;
        public override TaskAffinity Affinity { get; set; } = TaskAffinity.Concurrent;
        public override string Message { get; set; } = "Loading the history...";
    }
}
