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

    internal static ProcessResult Command(string args, string? workingDirectory = null, (string Name, string Value)[]? extraEnvironmentVariables = null)
    {
        var env = _lazyDotnetEnvironmentVariables.Value;
        if (extraEnvironmentVariables is not null)
        {
            env = new(env, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in extraEnvironmentVariables)
            {
                env[pair.Name] = pair.Value;
            }
        }

        return ProcessUtil.Run(
            "dotnet",
            args,
            workingDirectory: workingDirectory,
            environment: env);
    }
}
