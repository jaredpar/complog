﻿using System.Diagnostics.CodeAnalysis;
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
    private readonly Lazy<IReadOnlyCollection<string>> _lazyArguments;

    public string ProjectFilePath { get; }
    public string? CompilerFilePath { get; }
    public CompilerCallKind Kind { get; }
    public string? TargetFramework { get; }
    public bool IsCSharp { get; }
    internal object? OwnerState { get; }
    public string ProjectFileName { get; }
    public string ProjectDirectory { get; }

    public bool IsVisualBasic => !IsCSharp;

    internal CompilerCall(
        string projectFilePath,
        string? compilerFilePath,
        CompilerCallKind kind,
        string? targetFramework,
        bool isCSharp,
        Lazy<IReadOnlyCollection<string>> arguments,
        object? ownerState = null)
    {
        CompilerFilePath = compilerFilePath;
        ProjectFilePath = projectFilePath;
        Kind = kind;
        TargetFramework = targetFramework;
        IsCSharp = isCSharp;
        OwnerState = ownerState;
        _lazyArguments = arguments;
        ProjectFileName = Path.GetFileName(ProjectFilePath);
        ProjectDirectory = Path.GetDirectoryName(ProjectFilePath)!;
    }

    internal CompilerCall(
        string projectFilePath,
        string? compilerFilePath = null,
        CompilerCallKind kind = CompilerCallKind.Regular,
        string? targetFramework = null,
        bool isCSharp = true,
        string[]? arguments = null,
        object? ownerState = null)
        : this(
            projectFilePath,
            compilerFilePath,
            kind,
            targetFramework,
            isCSharp,
            new Lazy<IReadOnlyCollection<string>>(() => arguments ?? []),
            ownerState)
    {
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

    /// <summary>
    /// This returns the raw command line arguments passed to the compiler. None of the
    /// paths or arguments have been modified to be correct for the current machine.
    ///
    /// https://github.com/jaredpar/complog/issues/282
    /// </summary>
    public IReadOnlyCollection<string> GetArguments() => _lazyArguments.Value;

    [ExcludeFromCodeCoverage]
    public override string ToString() => GetDiagnosticName();
}
