using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog;

internal static class DotnetUtil
{
    internal static ProcessResult Command(string args, string? workingDirectory = null) =>
        ProcessUtil.Run(
            "dotnet",
            args,
            workingDirectory: workingDirectory);

    internal static ProcessResult New(string args, string? workingDirectory = null) => Command($"new {args}", workingDirectory);

    internal static ProcessResult Build(string args, string? workingDirectory = null) => Command($"build {args}", workingDirectory);

    internal static string? GetDotnetExecutable()
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "dotnet.exe"
            : "dotnet";

        // TODO: has to be a better way to find the runtime directory but this works for the moment
        var path = Path.GetDirectoryName(typeof(object).Assembly.Location);
        while (!string.IsNullOrEmpty(path))
        {
            var filePath = Path.Combine(path, fileName);
            if (Path.Exists(filePath))
            {
                return filePath;
            }

            path = Path.GetDirectoryName(path);
        }

        return null;
    }

    internal static List<string> GetSdkDirectories()
    {
        // TODO: has to be a better way to find the runtime directory but this works for the moment
        var path = Path.GetDirectoryName(typeof(object).Assembly.Location);
        while (Path.GetFileName(path) != "dotnet")
        {
            path = Path.GetDirectoryName(path);
        }

        if (path is null)
        {
            throw new Exception("Could not find dotnet directory");
        }

        var sdk = Path.Combine(path, "sdk");
        var sdks = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(sdk))
        {
            var sdkDir = Path.Combine(dir, "Roslyn", "bincore");
            if (Directory.Exists(sdkDir))
            {
                sdks.Add(dir);
            }
        }

        return sdks;
    }

    internal static string GetCompilerDll(bool isCsharp) => isCsharp ? "csc.exe" : "vbc.exe";

    internal static string GetDebugSolutionFileContent(
        string projectName,
        string exeFilePath,
        string workingDirectory,
        string arguments)
    {
        var content = $$"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.5.33414.496
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{911E67C6-3D85-4FCE-B560-20A9C3E3FF48}") = "{{projectName}}", "{{exeFilePath}}", "{113AF881-D859-4506-8004-22B7527DE638}"
                ProjectSection(DebuggerProjectSystem) = preProject
                    PortSupplier = 00000000-0000-0000-0000-000000000000
                    Executable = {{exeFilePath}}
                    RemoteMachine = {{Environment.MachineName}}
                    StartingDirectory = {{workingDirectory}}
                    Arguments = {{arguments}}
                    Environment = Default
                    LaunchingEngine = 2e36f1d4-b23c-435d-ab41-18e608940038
                    UseLegacyDebugEngines = No
                    LaunchSQLEngine = No
                    AttachLaunchAction = No
                    IORedirection = Auto
                EndProjectSection
            EndProject
            Global
                GlobalSection(SolutionConfigurationPlatforms) = preSolution
                    Release|x64 = Release|x64
                EndGlobalSection
                GlobalSection(ProjectConfigurationPlatforms) = postSolution
                    {113AF881-D859-4506-8004-22B7527DE638}.Release|x64.ActiveCfg = Release|x64
                EndGlobalSection
                GlobalSection(SolutionProperties) = preSolution
                    HideSolutionNode = FALSE
                EndGlobalSection
                GlobalSection(ExtensibilityGlobals) = postSolution
                    SolutionGuid = {D9E66FCE-34D1-4A3B-B39B-764EA4A52588}
                EndGlobalSection
            EndGlobal
            """;

        return content;
    }
}
