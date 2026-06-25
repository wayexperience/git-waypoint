using System.Collections.Generic;
using System.Text;
using System.Threading;
using Unity.Editor.Tasks;

namespace Unity.VersionControl.Git.Tasks
{
    using IO;

    public class GitUnlockTask : GitProcessTask<string>
    {
        private const string TaskName = "git lfs unlock";
        private readonly string arguments;
        private readonly List<string> argumentList;

        public GitUnlockTask(IPlatform platform,
            SPath path, bool force,
            CancellationToken token = default)
            : base(platform, null, outputProcessor: new StringOutputProcessor(), token: token)
        {
            Guard.ArgumentNotNullOrWhiteSpace(path, "path");

            Name = TaskName;
            var stringBuilder = new StringBuilder("lfs unlock ");

            argumentList = new List<string> { "lfs", "unlock" };
            if (force)
            {
                stringBuilder.Append("--force ");
                argumentList.Add("--force");
            }

            stringBuilder.Append("\"");
            stringBuilder.Append(path.ToString(SlashMode.Forward));
            stringBuilder.Append("\"");
            // Discrete path argument: no quoting, so spaces/quotes/option-looking names can't break parsing.
            argumentList.Add(path.ToString(SlashMode.Forward));

            arguments = stringBuilder.ToString();
        }

        // Unlock by lock id. git-lfs would otherwise resolve the path to an id by querying the server -
        // an extra round-trip we can skip because we already hold the id from the lock list.
        public GitUnlockTask(IPlatform platform,
            string id, bool force,
            CancellationToken token = default)
            : base(platform, null, outputProcessor: new StringOutputProcessor(), token: token)
        {
            Guard.ArgumentNotNullOrWhiteSpace(id, "id");

            Name = TaskName;
            var stringBuilder = new StringBuilder("lfs unlock ");

            argumentList = new List<string> { "lfs", "unlock" };
            if (force)
            {
                stringBuilder.Append("--force ");
                argumentList.Add("--force");
            }

            stringBuilder.Append("--id=");
            stringBuilder.Append(id);
            argumentList.Add("--id=" + id);

            arguments = stringBuilder.ToString();
        }

        public override string ProcessArguments => arguments;
        public override IReadOnlyList<string> ProcessArgumentList => argumentList;
        public override TaskAffinity Affinity { get; set; } = TaskAffinity.Exclusive;
        public override string Message { get; set; } = "Unlocking file...";

    }
}
