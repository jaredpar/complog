using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.Setup.Configuration;
using Basic.CompilerLog.Util;
using static Basic.CompilerLog.Util.ExportUtil;

namespace Basic.CompilerLog.App;

internal sealed record VisualStudioInstallation(string InstanceId, Version? Version, string InstallationPath)
{
    public string RoslynDirectory => Path.Combine(InstallationPath, "MSBuild", "Current", "Bin", "Roslyn");
}

internal static class VisualStudioUtil
{
    public static List<VisualStudioInstallation> GetInstallations()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Visual Studio discovery is only supported on Windows.");
        }

        return GetInstalledCompilersOnWindows();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static List<VisualStudioInstallation> GetInstalledCompilersOnWindows()
        {
            var installations = new List<VisualStudioInstallation>();
            var configuration = new SetupConfiguration();
            var configuration2 = (ISetupConfiguration2)configuration;
            var enumInstances = configuration2.EnumAllInstances();
            var instances = new ISetupInstance[1];

            while (true)
            {
                enumInstances.Next(1, instances, out var fetched);
                if (fetched == 0)
                {
                    break;
                }

                var instance2 = (ISetupInstance2)instances[0];
                var installationPath = instance2.GetInstallationPath();
                var instanceId = instance2.GetInstanceId();
                var versionString = instance2.GetInstallationVersion();
                Version? version = null;
                _ = Version.TryParse(versionString, out version);

                installations.Add(new(instanceId, version, installationPath));
            }

            return installations;
        }
    }

    internal static List<(string CompilerDirectory, string Name)> GetCompilerDirectories()
    {
        var list = new List<(string CompilerDirectory, string Name)>();
        var installations = GetInstallations()
            .OrderByDescending(i => i.Version)
            .ThenBy(compiler => compiler.InstanceId, StringComparer.Ordinal);
        foreach (var installation in installations)
        {
            var name = BuildVisualStudioInvocationName(installation);
            list.Add((installation.RoslynDirectory, name));
        }

        return list;

        static string BuildVisualStudioInvocationName(VisualStudioInstallation installation) =>
            installation.Version is null
                ? "unknown"
                : $"{installation.Version.Major}.{installation.Version.Minor}";
    }

}
