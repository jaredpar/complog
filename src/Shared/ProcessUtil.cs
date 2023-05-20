using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog;

internal readonly struct ProcessResult
{
    internal int ExitCode { get; }
    internal string StandardOut { get; }
    internal string StandardError { get; }

    internal bool Succeeded => ExitCode == 0;

    internal ProcessResult(int exitCode, string standardOut, string standardError)
    {
        ExitCode = exitCode;
        StandardOut = standardOut;
        StandardError = standardError;
    }
}

internal static class ProcessUtil
{
    internal static ProcessResult Run(
        string fileName,
        string args,
        string? workingDirectory = null)
    {
        var info = new ProcessStartInfo()
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(info)!;
        var standardOut = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();

        process.WaitForExit();
        return new ProcessResult(
            process.ExitCode,
            standardOut,
            standardError);
    }
}
