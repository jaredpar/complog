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

    /// <summary>
    /// Returns the sdk directories ordered by version ascending
    /// </summary>
    public static List<(string SdkDirectory, SdkVersion SdkVersion)> GetSdkDirectories(string? dotnetDirectory = null)
    {
        dotnetDirectory ??= GetDotnetDirectory();
        var sdk = Path.Combine(dotnetDirectory, "sdk");
        var sdks = new List<(string, SdkVersion)>();
        foreach (var dir in Directory.EnumerateDirectories(sdk))
        {
            var versionStr = Path.GetFileName(dir)!;
            if (!SdkVersion.TryParse(versionStr, out var version))
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

    internal static (string SdkDirectory, SdkVersion SdkVersion) GetLatestSdkDirectory(string? dotnetDirectory = null) =>
        GetSdkDirectories(dotnetDirectory)
            .OrderByDescending(x => x.SdkVersion)
            .First();

    internal static IReadOnlyList<CompilerInvocation> GetSdkCompilerInvocations(string? dotnetDirectory = null)
    {
        return GetSdkDirectories(dotnetDirectory)
            .OrderByDescending(sdk => sdk.SdkVersion)
            .Select(sdk => new CompilerInvocation(
                Name: Path.GetFileName(sdk.SdkDirectory)!,
                CSharpCommand: BuildSdkCommand(sdk.SdkDirectory, isCSharp: true),
                VisualBasicCommand: BuildSdkCommand(sdk.SdkDirectory, isCSharp: false)))
            .ToList();

        static string BuildSdkCommand(string sdkDir, bool isCSharp)
        {
            var binCoreDir = Path.Combine(sdkDir, "Roslyn", "bincore");
            var exePath = Path.Combine(binCoreDir, isCSharp ? "csc.exe" : "vbc.exe");
            if (File.Exists(exePath))
            {
                return $@"""{exePath}""";
            }

            var dllPath = Path.Combine(binCoreDir, isCSharp ? "csc.dll" : "vbc.dll");
            return $@"dotnet exec ""{dllPath}""";
        }
    }
}
