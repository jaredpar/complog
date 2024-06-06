using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Basic.CompilerLog.Util;

public sealed class CompilerAssemblyData(string filePath, AssemblyName assemblyName, string? commitHash)
{
    public string FilePath { get; } = filePath;
    public AssemblyName AssemblyName { get; } = assemblyName;
    public string? CommitHash { get; } = commitHash;

    public override string ToString() => $"{FilePath} {CommitHash}";
}

