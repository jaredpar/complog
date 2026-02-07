using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.Setup.Configuration;

namespace Basic.CompilerLog.Util;

internal sealed record VisualStudioCompiler(string InstanceId, Version? Version, string InstallationPath)
{
    public string RoslynDirectory => Path.Combine(InstallationPath, "MSBuild", "Current", "Bin", "Roslyn");

    public string GetCompilerPath(bool isCSharp) =>
        Path.Combine(RoslynDirectory, isCSharp ? "csc.exe" : "vbc.exe");
}

internal static class VisualStudioUtil
{
    public static IReadOnlyList<VisualStudioCompiler> GetInstalledCompilers()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Visual Studio discovery is only supported on Windows.");
        }

        return GetInstalledCompilersOnWindows();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IReadOnlyList<VisualStudioCompiler> GetInstalledCompilersOnWindows()
        {
            var compilers = new List<VisualStudioCompiler>();
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
                Version.TryParse(versionString, out version);

                var compiler = new VisualStudioCompiler(instanceId, version, installationPath);
                if (File.Exists(compiler.GetCompilerPath(isCSharp: true)))
                {
                    compilers.Add(compiler);
                }
            }

            return compilers;
        }
    }

    internal static IReadOnlyList<CompilerInvocation> GetCompilerInvocations(IReadOnlyList<VisualStudioCompiler> compilers)
    {
        return compilers
            .OrderByDescending(compiler => compiler.Version)
            .ThenBy(compiler => compiler.InstanceId, StringComparer.Ordinal)
            .Select(compiler => new CompilerInvocation(
                Name: BuildVisualStudioInvocationName(compiler),
                CSharpCommand: BuildVisualStudioCommand(compiler.GetCompilerPath(isCSharp: true)),
                VisualBasicCommand: BuildVisualStudioCommand(compiler.GetCompilerPath(isCSharp: false))))
            .ToList();
    }

    private static string BuildVisualStudioCommand(string compilerPath) => $@"""{compilerPath}""";

    private static string BuildVisualStudioInvocationName(VisualStudioCompiler compiler)
    {
        var versionText = compiler.Version is null
            ? "unknown"
            : $"{compiler.Version.Major}.{compiler.Version.Minor}";
        var instanceId = MakeSafeFileName(compiler.InstanceId);
        return $"vs-{versionText}-{instanceId}";
    }

    private static string MakeSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}
