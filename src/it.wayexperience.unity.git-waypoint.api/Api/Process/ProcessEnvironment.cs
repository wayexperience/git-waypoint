using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Editor.Tasks.Logging;

namespace Unity.VersionControl.Git
{
    using IO;
    using Unity.Editor.Tasks;

    public class ProcessEnvironment : IProcessEnvironment
    {
        private readonly IProcessEnvironment defaultEnvironment;
        protected IGitEnvironment GitEnvironment { get; private set; }
        protected ILogging Logger { get; private set; }

        private SPath basePath;
        private string[] envPath;
        private SPath gitInstallPath;
        private SPath libExecPath;

        public ProcessEnvironment(IProcessEnvironment defaultEnvironment, IGitEnvironment environment)
        {
            this.defaultEnvironment = defaultEnvironment;
            GitEnvironment = environment;

            Logger = LogHelper.GetLogger(GetType());
        }

        private void ResolvePaths()
        {
            basePath = libExecPath = SPath.Default;
            envPath = Array.Empty<string>();
            gitInstallPath = GitEnvironment.GitInstallPath;

            if (!gitInstallPath.IsInitialized)
                return;

            basePath = ResolveBasePath(Environment, gitInstallPath);
            envPath = CreateEnvPath(GitEnvironment, basePath).ToArray();
            if (ResolveGitExecPath(out var p))
                libExecPath = p;
        }

        public void Configure(ProcessStartInfo psi)
        {
            defaultEnvironment.Configure(psi);

            //if (gitInstallPath == SPath.Default || gitInstallPath != Environment.GitInstallPath)
                ResolvePaths();

            var pathEntries = new List<string>(envPath);
            string separator = GitEnvironment.IsWindows ? ";" : ":";

            // Unity launched from the Hub/Finder on macOS inherits a minimal PATH that usually doesn't
            // include Homebrew's bin directory, so `which git` can't find a Homebrew-installed git/git-lfs
            // and discovery fails. Add the standard Homebrew locations (Apple Silicon, then Intel) so both
            // the initial executable discovery and subsequent git invocations resolve.
            if (GitEnvironment.IsMac)
            {
                pathEntries.Add("/opt/homebrew/bin");
                pathEntries.Add("/usr/local/bin");
            }

            // we can only set this env var if there is a libexec/git-core. git will bypass internally bundled tools if this env var
            // is set, which will break Apple's system git on certain tools (like osx-credentialmanager)
            if (libExecPath.IsInitialized)
                psi.EnvironmentVariables["GIT_EXEC_PATH"] = libExecPath.ToString();

            pathEntries.Add("END");

            var path = string.Join(separator, pathEntries.ToArray()) + separator + GitEnvironment.Path;

            var pathEnvVarKey = GitEnvironment.GetEnvironmentVariableKey("PATH");
            psi.EnvironmentVariables[pathEnvVarKey] = path;

            //if (Environment.IsWindows)
            //{
            //    psi.EnvironmentVariables["PLINK_PROTOCOL"] = "ssh";
            //    psi.EnvironmentVariables["TERM"] = "msys";
            //}

            var httpProxy = GitEnvironment.GetEnvironmentVariable("HTTP_PROXY");
            if (!string.IsNullOrEmpty(httpProxy))
                psi.EnvironmentVariables["HTTP_PROXY"] = httpProxy;

            var httpsProxy = GitEnvironment.GetEnvironmentVariable("HTTPS_PROXY");
            if (!string.IsNullOrEmpty(httpsProxy))
                psi.EnvironmentVariables["HTTPS_PROXY"] = httpsProxy;
            psi.EnvironmentVariables["DISPLAY"] = "0";

            // Don't hang on a credential prompt with no terminal (GIT_TERMINAL_PROMPT=0), and bound the SSH
            // TCP connect so an unreachable server fails in seconds instead of blocking. We deliberately do
            // NOT force SSH BatchMode: that broke the normal case, where the SSH agent (e.g. 1Password) needs
            // to authenticate - with BatchMode it failed in ~2s on every op even when the agent was unlocked.
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            if (!GitEnvironment.IsWindows && string.IsNullOrEmpty(GitEnvironment.GetEnvironmentVariable("GIT_SSH_COMMAND")))
                psi.EnvironmentVariables["GIT_SSH_COMMAND"] = "ssh -o ConnectTimeout=15";

            // ===== TEMP DIAGNOSTIC (remove): trace what git/ssh actually do inside Unity, to pin the lock
            // hang. git's own trace -> git-trace.log; ssh -vvv (forced) -> ssh-trace.log. Both append. =====
            if (!GitEnvironment.IsWindows)
            {
                psi.EnvironmentVariables["GIT_TRACE"] = "/tmp/waygit-git-trace.log";
                psi.EnvironmentVariables["GIT_SSH_COMMAND"] = "ssh -vvv -o ConnectTimeout=15 -E /tmp/waygit-ssh-trace.log";
            }
            // ===== END TEMP DIAGNOSTIC =====

            // GUI-launched editors (Hub/Finder/Dock) don't inherit the user's login-shell environment, so
            // SSH_AUTH_SOCK is missing and git's ssh can't reach whatever agent the user runs (system
            // ssh-agent, a password manager, etc.) — SSH ops then fail without ever prompting. Import it
            // (and PATH) from the login shell. Agent-agnostic: we read whatever the shell exports.
            if (!GitEnvironment.IsWindows && string.IsNullOrEmpty(GitEnvironment.GetEnvironmentVariable("SSH_AUTH_SOCK")))
            {
                var sock = LoginShellValue("SSH_AUTH_SOCK");
                if (!string.IsNullOrEmpty(sock))
                    psi.EnvironmentVariables["SSH_AUTH_SOCK"] = sock;
            }

            if (!GitEnvironment.IsWindows)
            {
                psi.EnvironmentVariables["GIT_TEMPLATE_DIR"] = GitEnvironment.GitInstallPath.Combine("share/git-core/templates");
            }

            if (GitEnvironment.IsLinux)
            {
                psi.EnvironmentVariables["PREFIX"] = GitEnvironment.GitExecutablePath.Parent;
            }

            var sslCAInfo = GitEnvironment.GetEnvironmentVariable("GIT_SSL_CAINFO");
            if (string.IsNullOrEmpty(sslCAInfo))
            {
                var certFile = basePath.Combine("ssl/cacert.pem");
                if (certFile.FileExists())
                    psi.EnvironmentVariables["GIT_SSL_CAINFO"] = certFile.ToString();
            }
/*
            psi.WorkingDirectory = workingDirectory;
            psi.EnvironmentVariables["HOME"] = SPath.HomeDirectory;
            psi.EnvironmentVariables["TMP"] = psi.EnvironmentVariables["TEMP"] = SPath.SystemTemp;

            var path = Environment.Path;
            psi.EnvironmentVariables["GHU_WORKINGDIR"] = workingDirectory;
            var pathEnvVarKey = Environment.GetEnvironmentVariableKey("PATH");

            if (dontSetupGit)
            {
                psi.EnvironmentVariables["GHU_FULLPATH"] = path;
                psi.EnvironmentVariables[pathEnvVarKey] = path;
                return;
            }

            Guard.ArgumentNotNull(psi, "psi");

            var pathEntries = new List<string>();
            string separator = Environment.IsWindows ? ";" : ":";

            SPath libexecPath = SPath.Default;
            List<string> gitPathEntries = new List<string>();
            if (Environment.GitInstallPath.IsInitialized)
            {
                var gitPathRoot = Environment.GitExecutablePath.Resolve().Parent.Parent;
                var gitExecutableDir = Environment.GitExecutablePath.Parent; // original path to git (might be different from install path if it's a symlink)

                var baseExecPath = gitPathRoot;
                var binPath = baseExecPath;
                if (Environment.IsWindows)
                {
                    if (baseExecPath.DirectoryExists("mingw32"))
                        baseExecPath = baseExecPath.Combine("mingw32");
                    else
                        baseExecPath = baseExecPath.Combine("mingw64");
                    binPath = baseExecPath.Combine("bin");
                }

                libexecPath = baseExecPath.Combine("libexec", "git-core");
                if (!libexecPath.DirectoryExists())
                    libexecPath = SPath.Default;

                if (Environment.IsWindows)
                {
                    gitPathEntries.AddRange(new[] { gitPathRoot.Combine("cmd").ToString(), gitPathRoot.Combine("usr", "bin") });
                }
                else
                {
                    gitPathEntries.Add(gitExecutableDir.ToString());
                }

                if (libexecPath.IsInitialized)
                    gitPathEntries.Add(libexecPath);
                gitPathEntries.Add(binPath);

                // we can only set this env var if there is a libexec/git-core. git will bypass internally bundled tools if this env var
                // is set, which will break Apple's system git on certain tools (like osx-credentialmanager)
                if (libexecPath.IsInitialized)
                    psi.EnvironmentVariables["GIT_EXEC_PATH"] = libexecPath.ToString();
            }

            if (Environment.GitLfsInstallPath.IsInitialized && libexecPath != Environment.GitLfsInstallPath)
            {
                pathEntries.Add(Environment.GitLfsInstallPath);
            }
            if (gitPathEntries.Count > 0)
                pathEntries.AddRange(gitPathEntries);

            pathEntries.Add("END");

            path = string.Join(separator, pathEntries.ToArray()) + separator + path;

            psi.EnvironmentVariables["GHU_FULLPATH"] = path;
            psi.EnvironmentVariables[pathEnvVarKey] = path;

            //TODO: Remove with Git LFS Locking becomes standard
            psi.EnvironmentVariables["GITLFSLOCKSENABLED"] = "1";

            if (Environment.IsWindows)
            {
                psi.EnvironmentVariables["PLINK_PROTOCOL"] = "ssh";
                psi.EnvironmentVariables["TERM"] = "msys";
            }

            var httpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
            if (!string.IsNullOrEmpty(httpProxy))
                psi.EnvironmentVariables["HTTP_PROXY"] = httpProxy;

            var httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
            if (!string.IsNullOrEmpty(httpsProxy))
                psi.EnvironmentVariables["HTTPS_PROXY"] = httpsProxy;
            psi.EnvironmentVariables["DISPLAY"] = "0";
*/
        }


        // GUI-launched editors miss the login-shell environment. Read a single variable (e.g.
        // SSH_AUTH_SOCK) from the user's login+interactive shell so we pick up whatever they actually
        // configured, regardless of which agent/tooling they use. Cached: the shell runs at most once
        // per variable. Returns null on any failure.
        private static readonly Dictionary<string, string> loginShellCache = new Dictionary<string, string>();
        private static string LoginShellValue(string name)
        {
            lock (loginShellCache)
            {
                if (loginShellCache.TryGetValue(name, out var cached))
                    return cached;
            }

            string result = null;
            try
            {
                var shell = System.Environment.GetEnvironmentVariable("SHELL");
                if (string.IsNullOrEmpty(shell))
                    shell = "/bin/zsh";

                var script = "printf '%s' \"${" + name + "}\"";
                var psi = new ProcessStartInfo
                {
                    FileName = shell,
                    Arguments = "-lic \"" + script.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p != null)
                    {
                        var outp = p.StandardOutput.ReadToEnd();
                        if (p.WaitForExit(4000))
                            result = string.IsNullOrWhiteSpace(outp) ? null : outp.Trim();
                        else
                            try { p.Kill(); } catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger<ProcessEnvironment>().Trace(ex, "Could not read {0} from the login shell", name);
            }

            lock (loginShellCache)
                loginShellCache[name] = result;
            return result;
        }

        private bool ResolveGitExecPath(out SPath path)
        {
            path = ResolveBasePath(Environment, gitInstallPath).Combine("libexec", "git-core");
            return path.DirectoryExists();
        }

        private static SPath ResolveBasePath(IEnvironment environment, SPath installPath)
        {
            var path = installPath;

            if (!environment.IsWindows)
                return path;

            if (environment.Is32Bit)
                path = installPath.Combine("mingw32");
            else
                path = installPath.Combine("mingw64");

            return path;
        }

        private static IEnumerable<string> CreateEnvPath(IGitEnvironment environment, SPath basePath)
        {
            yield return environment.GitExecutablePath.Parent.ToString();
            yield return basePath.Combine("bin").ToString();
            if (environment.IsWindows)
                yield return environment.GitInstallPath.Combine("usr/bin").ToString();
            if (environment.GitInstallPath.IsInitialized && environment.GitLfsExecutablePath.Parent != environment.GitExecutablePath.Parent)
                yield return environment.GitLfsExecutablePath.Parent.ToString();
        }

        public IEnvironment Environment => defaultEnvironment.Environment;
    }
}
