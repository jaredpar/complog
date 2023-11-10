using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Printing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MessagePack.Formatters;

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
        string? workingDirectory = null,
        Dictionary<string, string>? environment = null)
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

        if (environment is not null)
        {
            info.Environment.Clear();
            foreach (var tuple in environment)
            {
                info.Environment.Add(tuple.Key, tuple.Value);
            }
        }

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
