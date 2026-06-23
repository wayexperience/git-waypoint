#pragma warning disable 169,649

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Editor.Tasks;
using Unity.Editor.Tasks.Extensions;
using Unity.Editor.Tasks.Helpers;
using Unity.Editor.Tasks.Logging;

namespace Unity.VersionControl.Git
{
    using Json;
    using IO;

    public class DugiteReleaseManifest
    {
        private long id;
        private UriString url;
        private UriString assets_url;
        private string tag_name;
        private string name;
        private DateTimeOffset published_at;
        private List<Asset> assets;

        public struct Asset
        {
            private long id;
            private string name;
            private string content_type;
            private long size;
            private DateTimeOffset updated_at;
            private UriString browser_download_url;


            [NotSerialized] public string Name => name;
            [NotSerialized] public string ContentType => content_type;
            [NotSerialized] public DateTimeOffset Timestamp => updated_at;
            [NotSerialized] public UriString Url => browser_download_url;
            public string Hash { get; set; }
        }

        [NotSerialized] public TheVersion Version => TheVersion.Parse(tag_name.Substring(1));

        [NotSerialized] public DateTimeOffset Timestamp => published_at;

        [NotSerialized] public Asset DugitePackage { get; private set; }

        [NotSerialized] public IEnumerable<Asset> Assets => assets;

        private (Asset zipFile, Asset shaFile) GetAsset(IEnvironment environment)
        {
            // desktop/dugite-native asset names are always {os}-{arch}, e.g. windows-x64,
            // macOS-arm64, macOS-x64, ubuntu-x64. Pick the slice matching the running editor.
            var os = environment.IsWindows ? "windows" : environment.IsMac ? "macOS" : "ubuntu";
            var assetName = $"{os}-{GetArch(environment)}.tar.gz";
            return (assets.FirstOrDefault(x => x.Name.EndsWith(assetName)),
                assets.FirstOrDefault(x => x.Name.EndsWith(assetName + ".sha256")));
        }

        private static string GetArch(IEnvironment environment)
        {
            switch (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture)
            {
                case System.Runtime.InteropServices.Architecture.Arm64: return "arm64";
                case System.Runtime.InteropServices.Architecture.Arm: return "arm";
                case System.Runtime.InteropServices.Architecture.X86: return "x86";
                default: return environment.Is32Bit ? "x86" : "x64";
            }
        }

        public static DugiteReleaseManifest Load(ITaskManager taskManager, SPath manifestFile,
            SPath userCachePath,
            IGitEnvironment environment)
        {
            var manifest = manifestFile.ReadAllText().FromJson<DugiteReleaseManifest>(true, false);
            var (zipAsset, shaAsset) = manifest.GetAsset(environment);
            var shaAssetPath = userCachePath.Combine("downloads", shaAsset.Name);
            if (!shaAssetPath.FileExists())
            {
                var downloader = new Downloader(taskManager);
                downloader.QueueDownload(shaAsset.Url, shaAssetPath.Parent, shaAssetPath.FileName);
                downloader.RunSynchronously();
            }
            // The .sha256 asset is the bare hex digest with a trailing newline; integrity check
            // compares with Equals, so strip surrounding whitespace or it never matches.
            zipAsset.Hash = shaAssetPath.ReadAllText().Trim();
            manifest.DugitePackage = zipAsset;
            return manifest;
        }

        public static DugiteReleaseManifest Load(ITaskManager taskManager, SPath localCacheFile,
            UriString packageFeed, IGitEnvironment environment,
            bool alwaysDownload = false)
        {
            DugiteReleaseManifest package = null;
            var filename = localCacheFile.FileName;
            var cacheDir = localCacheFile.Parent;
            var key = localCacheFile.FileNameWithoutExtension + "_updatelastCheckTime";
            var now = DateTimeOffset.Now;

            if (!localCacheFile.FileExists() ||
                (alwaysDownload || now.Date > environment.UserSettings.Get<DateTimeOffset>(key).Date))
            {
                var result = new DownloadTask(taskManager, packageFeed,
                    localCacheFile.Parent, filename)
                .Catch(ex => {
                    Logger.Warning(@"Error downloading package feed:{0} ""{1}"" Message:""{2}""", packageFeed,
                        ex.GetType().ToString(), ex.GetExceptionMessageShort());
                    return true;
                }).RunSynchronously();
                localCacheFile = result.ToSPath();
                if (localCacheFile.IsInitialized && !alwaysDownload)
                    environment.UserSettings.Set<DateTimeOffset>(key, now);
            }

            if (!localCacheFile.IsInitialized)
            {
                // try from assembly resources
                localCacheFile = AssemblyResources.ToFile(ResourceType.Platform, filename, cacheDir, environment);
            }

            if (localCacheFile.IsInitialized)
            {
                try
                {
                    package = Load(taskManager, localCacheFile, cacheDir, environment);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            }
            return package;
        }

        private static ILogging Logger { get; } = LogHelper.GetLogger<DugiteReleaseManifest>();
    }
}
