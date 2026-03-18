using System.Diagnostics.CodeAnalysis;

namespace Basic.CompilerLog.Util;

public sealed class MSBuildData(string? processPath, string? msbuildPath, string? commandLine, string? msbuildVersion)
{
    public string? ProcessPath { get; } = processPath;
    public string? MSBuildPath { get; } = msbuildPath;
    public string? CommandLine { get; } = commandLine;
    public string? MSBuildVersion { get; } = msbuildVersion;

    [ExcludeFromCodeCoverage]
    public override string ToString() => $"{MSBuildPath} {CommandLine}";
}
