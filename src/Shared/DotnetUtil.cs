using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration.Internal;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog;

internal static class DotnetUtil
{
    private static readonly Lazy<Dictionary<string, string>> _lazyDotnetEnvironmentVariables = new(CreateDotnetEnvironmentVariables);

    private static Dictionary<string, string> CreateDotnetEnvironmentVariables()
    {
        // The CLI, particularly when run from dotnet test, will set the MSBuildSDKsPath environment variable
        // to point to the current SDK. That could be an SDK that is higher than the version that our tests
        // are executing under. For example `dotnet test` could spawn an 8.0 process but we end up testing
        // the 7.0.400 SDK. This environment variable though will point to 8.0 and end up causing load 
        // issues. Clear it out here so that the `dotnet` commands have a fresh context.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (!string.Equals(key, "MSBuildSDKsPath", StringComparison.OrdinalIgnoreCase))
            {
                map.Add(key, (string)entry.Value!);

            }
        }
        return map;
    }

    internal static ProcessResult Command(string args, string? workingDirectory = null) =>
        ProcessUtil.Run(
            "dotnet",
            args,
            workingDirectory: workingDirectory,
            environment: _lazyDotnetEnvironmentVariables.Value);

    internal static void CommandOrThrow(string args, string? workingDirectory = null)
    {
        if (!Command(args, workingDirectory).Succeeded)
        {
            throw new Exception("Command failed");
        }
    }

    internal static ProcessResult New(string args, string? workingDirectory = null) => Command($"new {args}", workingDirectory);

    internal static ProcessResult Build(string args, string? workingDirectory = null) => Command($"build {args}", workingDirectory);

    internal static void AddProjectProperty(string property, string workingDirectory)
    {
        var projectFile = Directory.EnumerateFiles(workingDirectory, "*proj").Single();
        var lines = File.ReadAllLines(projectFile);
        using var writer = new StreamWriter(projectFile, append: false);
        foreach (var line in lines)
        {
            if (line.Contains("</PropertyGroup>"))
            {
                writer.WriteLine(property);
            }

            writer.WriteLine(line);
        }
    }

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
