using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.UnitTests;

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
}
