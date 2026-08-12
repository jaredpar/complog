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
    private static readonly Lazy<Dictionary<string, string>> s_lazyDotnetEnvironmentVariables = new(CreateDotnetEnvironmentVariables);

    private static Dictionary<string, string> CreateDotnetEnvironmentVariables() =>
        CreateDotnetEnvironmentVariables(Environment.GetEnvironmentVariables());

    /// <summary>
    /// Copies the parent environment without MSBuild settings that are unsafe for child commands.
    /// </summary>
    /// <param name="environmentVariables">The parent process environment variables.</param>
    internal static Dictionary<string, string> CreateDotnetEnvironmentVariables(IDictionary environmentVariables)
    {
        // The .NET CLI, particularly under dotnet test, sets these paths to its selected SDK. A child
        // command can select a different SDK from its working directory and global.json roll-forward
        // policy, so retaining either path can mix targets and tasks from the parent SDK with MSBuild
        // assemblies from the child SDK. Newer task API dependencies made this latent mismatch fail
        // during task loading. DOTNET_HOST_PATH is retained because it identifies the shared muxer.
        // MSBuildExtensionsPath32 and MSBuildExtensionsPath64 are retained because the CLI does not set
        // them. Other MSBuild settings are retained unless they are known to be unsafe for child commands.
        // https://github.com/jaredpar/complog/pull/73
        //
        // MSBUILDENSURESTDOUTFORTASKPROCESSES makes reusable worker nodes inherit the redirected stdout
        // handle. ProcessUtil synchronously reads stdout to EOF, so those nodes can block it until they exit.
        // https://github.com/jaredpar/complog/pull/375
        var comparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var map = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in environmentVariables)
        {
            var key = (string)entry.Key;
            if (!string.Equals(key, "MSBuildExtensionsPath", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "MSBuildSDKsPath", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "MSBUILDENSURESTDOUTFORTASKPROCESSES", StringComparison.OrdinalIgnoreCase))
            {
                map[key] = (string)entry.Value!;
            }
        }

        return map;
    }

    internal static ProcessResult Command(string args, string? workingDirectory = null) =>
        ProcessUtil.Run(
            "dotnet",
            args,
            workingDirectory: workingDirectory,
            environment: s_lazyDotnetEnvironmentVariables.Value);
}
