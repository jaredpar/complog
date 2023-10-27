using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Basic.CompilerLog.Util;

public enum CompilerCallKind
{
    /// <summary>
    /// Standard compilation call that goes through the C# / VB targets
    /// </summary>
    Regular,

    /// <summary>
    /// Compilation to build a satellite assembly
    /// </summary>
    Satellite,

    /// <summary>
    /// Temporary assembly generated for WPF projects
    /// </summary>
    WpfTemporaryCompile,

    /// <summary>
    /// Compilation that occurs in the XAML pipeline to create a temporary assembly used 
    /// to reflect on to generate types for the real compilation
    /// </summary>
    XamlPreCompile,

    /// <summary>
    /// Compilation that doesn't fit existing classifications
    /// </summary>
    Unknown
}

public sealed class CompilerCall
{
    public string ProjectFilePath { get; }
    public CompilerCallKind Kind { get; }
    public string? TargetFramework { get; }
    public bool IsCSharp { get; }
    public string[] Arguments { get; }
    internal int? Index { get; }

    public bool IsVisualBasic => !IsCSharp;
    public string ProjectFileName => Path.GetFileName(ProjectFilePath);
    public string ProjectDirectory => Path.GetDirectoryName(ProjectFilePath)!;

    internal CompilerCall(
        string projectFilePath,
        CompilerCallKind kind,
        string? targetFramework,
        bool isCSharp,
        string[] arguments,
        int? index)
    {
        ProjectFilePath = projectFilePath;
        Kind = kind;
        TargetFramework = targetFramework;
        IsCSharp = isCSharp;
        Arguments = arguments;
        Index = index;
    }

    public string GetDiagnosticName() 
    {
        var baseName = string.IsNullOrEmpty(TargetFramework)
            ? ProjectFileName
            : $"{ProjectFileName} ({TargetFramework})";
        if (Kind != CompilerCallKind.Regular)
        {
            return $"{baseName} ({Kind})";
        }

        return baseName;
    }

    internal CommandLineArguments ParseArguments()
    {
        var baseDirectory = Path.GetDirectoryName(ProjectFilePath)!;
        return IsCSharp
            ? CSharpCommandLineParser.Default.Parse(Arguments, baseDirectory, sdkDirectory: null, additionalReferenceDirectories: null)
            : VisualBasicCommandLineParser.Default.Parse(Arguments, baseDirectory, sdkDirectory: null, additionalReferenceDirectories: null);
    }
}
