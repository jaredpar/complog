using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Basic.CompilerLog.Util;

public enum CompilerCallKind
{
    Regular,
    Satellite
}

public sealed class CompilerCall
{
    public string ProjectFile { get; }
    public CompilerCallKind Kind { get; }
    public string? TargetFramework { get; }
    public bool IsCSharp { get; }
    public string[] Arguments { get; }

    public bool IsVisualBasic => !IsCSharp;

    internal CompilerCall(string projectFile, CompilerCallKind kind, string? targetFramework, bool isCSharp, string[] arguments)
    {
        ProjectFile = projectFile;
        Kind = kind;
        TargetFramework = targetFramework;
        IsCSharp = isCSharp;
        Arguments = arguments;
    }
}
