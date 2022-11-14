using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog;

internal static class DotnetUtil
{
    internal static int Dotnet(string args, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo()
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        var process = Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    internal static int New(string args, string? workingDirectory = null) => Dotnet($"new {args}", workingDirectory);

    internal static int Build(string args, string? workingDirectory = null) => Dotnet($"build {args}", workingDirectory);

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
            var sdkDir = Path.Combine(dir, @"Roslyn\bincore");
            if (Directory.Exists(sdkDir))
            {
                sdks.Add(dir);
            }
        }

        return sdks;
    }
}
