using System.Collections.Generic;
using System.Threading;
using Unity.Editor.Tasks;

namespace Unity.VersionControl.Git.Tasks
{
    public class GitLockTask : GitProcessTask<string>
    {
        private const string TaskName = "git lfs lock";
        private readonly string args;
        private readonly string path;

        public GitLockTask(IPlatform platform,
                string path,
                CancellationToken token = default)
            : base(platform, null, outputProcessor: new StringOutputProcessor(), token: token)
        {
            Name = TaskName;
            Guard.ArgumentNotNullOrWhiteSpace(path, "path");
            this.path = path;
            args = $"lfs lock \"{path}\"";
        }

        public override string ProcessArguments => args;
        // Pass the path as a discrete argument so spaces/quotes/option-looking names can't break parsing.
        public override IReadOnlyList<string> ProcessArgumentList => new[] { "lfs", "lock", path };
        public override TaskAffinity Affinity { get; set; } = TaskAffinity.Exclusive;
        public override string Message { get; set; } = "Locking file...";
    }
}
