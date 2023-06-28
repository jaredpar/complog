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

    internal static void CommandOrThrow(string args, string? workingDirectory = null)
    {
        if (!Command(args, workingDirectory).Succeeded)
        {
            throw new Exception("Command failed");
        }
    }

    internal static ProcessResult New(string args, string? workingDirectory = null) => Command($"new {args}", workingDirectory);

    internal static ProcessResult Build(string args, string? workingDirectory = null) => Command($"build {args}", workingDirectory);

    internal static List<string> GetSdkDirectories()
    {
        // TODO: has to be a better way to find the runtime directory but this works for the moment
        var path = Path.GetDirectoryName(typeof(object).Assembly.Location);
        while (path is not null && !IsDotNetDir(path))
        {
            path = Path.GetDirectoryName(path);
        }

        if (path is null)
        {
            throw new Exception("Could not find dotnet directory");
        }

        return GetSdkDirectories(path);

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
    }

    internal static List<string> GetSdkDirectories(string dotnetDirectory)
    {
        var sdk = Path.Combine(dotnetDirectory, "sdk");
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
}
