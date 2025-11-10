using NuGet.Versioning;
using System.Runtime.InteropServices;

namespace Basic.CompilerLog.Util;

public static class SdkUtil
{
    public static string GetDotnetDirectory(string? path = null)
    {
        path ??= GetDefaultSearchPoint();
        var initialPath = path;

        while (path is not null && !IsDotNetDir(path))
        {
            path = Path.GetDirectoryName(path);
        }

        if (path is null)
        {
            throw new Exception($"Could not find dotnet directory using initial path {initialPath}");
        }

        return path;

        static bool IsDotNetDir(string path)
        {
            var appName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "dotnet.exe"
                : "dotnet";

            return
                File.Exists(Path.Combine(path, appName)) &&
                Directory.Exists(Path.Combine(path, "sdk")) &&
                Directory.Exists(Path.Combine(path, "host"));
        }

        static string GetDefaultSearchPoint()
        {
#if NET
            // TODO: has to be a better way to find the runtime directory but this works for the moment
            return Path.GetDirectoryName(typeof(object).Assembly.Location)!;
#else
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
#endif
        }
    }

    public static List<string> GetSdkDirectories(string? dotnetDirectory = null) =>
        GetSdkDirectoriesAndVersion(dotnetDirectory)
            .Select(x => x.SdkDirectory)
            .ToList();

    public static List<(string SdkDirectory, NuGetVersion SdkVersion)> GetSdkDirectoriesAndVersion(string? dotnetDirectory = null)
    {
        dotnetDirectory ??= GetDotnetDirectory();
        var sdk = Path.Combine(dotnetDirectory, "sdk");
        var sdks = new List<(string, NuGetVersion)>();
        foreach (var dir in Directory.EnumerateDirectories(sdk))
        {
            var versionStr = Path.GetFileName(dir)!;
            if (!NuGetVersion.TryParse(versionStr, out var version))
            {
                continue;
            }

            var sdkDir = Path.Combine(dir, "Roslyn", "bincore");
            if (Directory.Exists(sdkDir))
            {
                sdks.Add((dir, version));
            }
        }

        return sdks;
    }

    public static (string SdkDirectory, NuGetVersion SdkVersion) GetLatestSdkDirectories(string? dotnetDirectory = null) =>
        GetSdkDirectoriesAndVersion(dotnetDirectory)
            .OrderByDescending(x => x.SdkVersion)
            .First();
}
