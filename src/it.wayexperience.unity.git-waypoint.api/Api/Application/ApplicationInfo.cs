#pragma warning disable 436
using Unity.VersionControl.Git;
using Unity.VersionControl.Git.IO;

namespace Unity.VersionControl.Git
{

    public static partial class ApplicationInfo
    {
#if GFU_DEBUG
        public const string ApplicationName = "Git for Unity Debug";
        public const string ApplicationProvider = "Unity";
        public const string ApplicationSafeName = "GitForUnityDebug";
#else
        public const string ApplicationName = "GitForUnity";
        public const string ApplicationProvider = "Unity";
        public const string ApplicationSafeName = "GitForUnity";
#endif
        public const string ApplicationDescription = "Git for Unity";

        public static string Version { get; } =  ThisAssembly.GetInformationalVersion();
    }
}

internal static partial class ThisAssembly {
        // Used when no build-stamped version is available (embedded source / unstamped package),
        // so the editor reports a real version instead of "0".
        public const string FallbackVersion = "0.1.2";

    public static string GetInformationalVersion()
    {
        try
        {
            var attr = System.Attribute.GetCustomAttribute(typeof(ThisAssembly).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute)) as System.Reflection.AssemblyInformationalVersionAttribute;
            if (attr != null && !string.IsNullOrEmpty(attr.InformationalVersion) && !attr.InformationalVersion.StartsWith("0"))
                return attr.InformationalVersion;
            var basePath = Platform.Instance?.Environment.ExtensionInstallPath ?? SPath.Default;
            if (!basePath.IsInitialized)
                return FallbackVersion;
            var versionFile = basePath.Parent.Combine("version.json");
            if (!versionFile.FileExists())
                return FallbackVersion;
            var version = versionFile.ReadAllText().FromJson<VersionJson>(true);
            var parsed = TheVersion.Parse(version.version).Version;
            return string.IsNullOrEmpty(parsed) || parsed.StartsWith("0") ? FallbackVersion : parsed;
        }
        catch
        {
            return FallbackVersion;
        }
    }

    public class VersionJson
    {
        public string version;
    }
}
