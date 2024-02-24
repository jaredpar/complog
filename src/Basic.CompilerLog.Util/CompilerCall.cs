using System.Net.Http.Headers;
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

/// <summary>
/// Represents a call to the compiler. The file paths and arguments provided here are correct 
/// for the machine on which the compiler was run. They cannot be relied on to be correct on 
/// machines where a compiler log is rehydrated.
/// </summary>
public sealed class CompilerCall
{
    private readonly Lazy<string[]> _lazyArguments;
    private readonly Lazy<CommandLineArguments> _lazyParsedArgumets;

    public string? CompilerFilePath { get; }
    public string ProjectFilePath { get; }
    public CompilerCallKind Kind { get; }
    public string? TargetFramework { get; }
    public bool IsCSharp { get; }
    internal int? Index { get; }

    public bool IsVisualBasic => !IsCSharp;
    public string ProjectFileName => Path.GetFileName(ProjectFilePath);
    public string ProjectDirectory => Path.GetDirectoryName(ProjectFilePath)!;

    internal CompilerCall(
        string? compilerFilePath,
        string projectFilePath,
        CompilerCallKind kind,
        string? targetFramework,
        bool isCSharp,
        Lazy<string[]> arguments,
        int? index)
    {
        CompilerFilePath = compilerFilePath;
        ProjectFilePath = projectFilePath;
        Kind = kind;
        TargetFramework = targetFramework;
        IsCSharp = isCSharp;
        Index = index;
        _lazyArguments = arguments;
        _lazyParsedArgumets = new Lazy<CommandLineArguments>(ParseArgumentsCore);
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

    public string[] GetArguments() => _lazyArguments.Value;

    internal CommandLineArguments ParseArguments() => _lazyParsedArgumets.Value;

    private CommandLineArguments ParseArgumentsCore()
    {
        var baseDirectory = Path.GetDirectoryName(ProjectFilePath)!;
        return IsCSharp
            ? CSharpCommandLineParser.Default.Parse(GetArguments(), baseDirectory, sdkDirectory: null, additionalReferenceDirectories: null)
            : VisualBasicCommandLineParser.Default.Parse(GetArguments(), baseDirectory, sdkDirectory: null, additionalReferenceDirectories: null);
    }
}
