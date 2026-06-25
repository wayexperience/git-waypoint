#pragma warning disable 436
using Unity.VersionControl.Git;
using Unity.VersionControl.Git.IO;

namespace Unity.VersionControl.Git
{

    public static partial class ApplicationInfo
    {
#if GFU_DEBUG
        public const string ApplicationName = "Git Waypoint Debug";
        public const string ApplicationProvider = "Unity";
        public const string ApplicationSafeName = "GitWaypointDebug";
#else
        // Drives the per-user folders ~/Library/Logs/<ApplicationName>/ and
        // ~/Library/Application Support/<ApplicationName>/ (bundled-git cache), so keep it on-brand.
        public const string ApplicationName = "GitWaypoint";
        public const string ApplicationProvider = "WAY Experience"; // metadata only (not referenced anywhere)
        public const string ApplicationSafeName = "GitWaypoint";
#endif
        public const string ApplicationDescription = "Git Waypoint";

        public static string Version { get; } =  ThisAssembly.GetInformationalVersion();
    }
}

internal static partial class ThisAssembly {
        // Last resort only (reached if both the stamped attribute and the Package Manager lookup fail).
        // The real version comes from package.json via PackageInfo.FindForAssembly, so this rarely shows.
        public const string FallbackVersion = "0.1.18";

    public static string GetInformationalVersion()
    {
        // 1) CI-stamped assembly version (nbgv), when a real build produced one.
        try
        {
            var attr = System.Attribute.GetCustomAttribute(typeof(ThisAssembly).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute)) as System.Reflection.AssemblyInformationalVersionAttribute;
            if (attr != null && !string.IsNullOrEmpty(attr.InformationalVersion) && !attr.InformationalVersion.StartsWith("0"))
                return attr.InformationalVersion;
        }
        catch { }

        // 2) The installed package's own version - this reads package.json via the Package Manager, so the
        // reported version always matches the package that's actually running (no manual bump to keep in sync).
        try
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ThisAssembly).Assembly);
            if (pkg != null && !string.IsNullOrEmpty(pkg.version))
                return pkg.version;
        }
        catch { }

        // 3) Legacy: a version.json next to the install path.
        try
        {
            var basePath = Platform.Instance?.Environment.ExtensionInstallPath ?? SPath.Default;
            if (basePath.IsInitialized)
            {
                var versionFile = basePath.Parent.Combine("version.json");
                if (versionFile.FileExists())
                {
                    var version = versionFile.ReadAllText().FromJson<VersionJson>(true);
                    var parsed = TheVersion.Parse(version.version).Version;
                    if (!string.IsNullOrEmpty(parsed) && !parsed.StartsWith("0"))
                        return parsed;
                }
            }
        }
        catch { }

        return FallbackVersion;
    }

    public class VersionJson
    {
        public string version;
    }
}
