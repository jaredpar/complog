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

namespace Basic.CompilerLog.Util;

public static class SdkUtil
{
    public static string GetDotnetDirectory(string? path = null)
    {
        // TODO: has to be a better way to find the runtime directory but this works for the moment
        path ??= Path.GetDirectoryName(typeof(object).Assembly.Location);
        while (path is not null && !IsDotNetDir(path))
        {
            path = Path.GetDirectoryName(path);
        }

        if (path is null)
        {
            throw new Exception("Could not find dotnet directory");
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
    }

    public static List<string> GetSdkDirectories(string? dotnetDirectory = null)
    {
        dotnetDirectory ??= GetDotnetDirectory();
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
