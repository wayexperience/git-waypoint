using System.Text;
using System.Threading;
using Unity.Editor.Tasks;
using static System.String;

namespace Unity.VersionControl.Git.Tasks
{
    public class GitPullTask : GitProcessTask<string>
    {
        private const string TaskName = "git pull";
        private readonly string arguments;

        public GitPullTask(IPlatform platform,
            string remote, string branch,
            bool rebase = false,
            CancellationToken token = default)
            : base(platform, null, outputProcessor: new StringOutputProcessor(), token: token)
        {
            Name = TaskName;
            var stringBuilder = new StringBuilder();
            stringBuilder.Append("pull");

            if (rebase)
                // --autostash so a rebase works even with uncommitted changes: git stashes them, rebases,
                // then re-applies them (rebase requires a clean tree otherwise).
                stringBuilder.Append(" --rebase --autostash");
            else
                // Be explicit about merging. Since git 2.27 a bare `git pull` aborts on divergent branches
                // ("Need to specify how to reconcile") unless a strategy is set; --no-edit keeps the merge
                // non-interactive so the child process can't hang waiting on a commit-message editor.
                stringBuilder.Append(" --no-rebase --no-edit");

            if (!IsNullOrEmpty(remote))
            {
                stringBuilder.Append(" ");
                stringBuilder.Append(remote);
            }

            if (!IsNullOrEmpty(branch))
            {
                stringBuilder.Append(" ");
                stringBuilder.Append(branch);
            }

            arguments = stringBuilder.ToString();
        }

        public override string ProcessArguments => arguments;
        public override TaskAffinity Affinity { get; set; } = TaskAffinity.Exclusive;
        public override string Message { get; set; } = "Pulling...";
    }
}
