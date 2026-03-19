using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Options;

namespace Basic.CompilerLog.App;
internal sealed class FilterOptionSet : OptionSet
{
    private string? _customCompilerFilePath;
    private bool _hasAnalyzerOptions;
    private BasicAnalyzerKind _basicAnalyzerKind;
    private bool _stripReadyToRun = true;

    internal List<string> TargetFrameworks { get; } = new();
    internal bool IncludeAllKinds { get; set; }
    internal List<string> ProjectNames { get; } = new();
    internal bool Help { get; set; }

    /// <summary>
    /// This is the path to a custom compiler to use for replaying compilations.
    /// </summary>
    internal string? CustomCompilerFilePath
    {
        get
        {
            CheckHasAnalyzerOptions();
            return _customCompilerFilePath;
        }
    }

    internal BasicAnalyzerKind BasicAnalyzerKind
    {
        get
        {
            CheckHasAnalyzerOptions();
            return _basicAnalyzerKind;
        }
    }

    internal bool IncludeAnalyzers
    {
        get
        {
            CheckHasAnalyzerOptions();
            return _basicAnalyzerKind != BasicAnalyzerKind.None;
        }
    }

    /// <summary>
    /// When <see langword="true"/> (the default), ReadyToRun (R2R) analyzer assemblies that target
    /// a different architecture than the current process are stripped to IL-only. Set via
    /// <c>--no-strip</c> to <see langword="false"/> to disable stripping entirely.
    /// </summary>
    internal bool StripReadyToRun
    {
        get
        {
            CheckHasAnalyzerOptions();
            return _stripReadyToRun;
        }
    }

    internal FilterOptionSet(bool analyzers = false)
    {
        Add("all", "include all compilation kinds", i => { if (i is not null) IncludeAllKinds = true; });
        Add("f|framework=", "include only compilations for the target framework (allows multiple)", TargetFrameworks.Add);
        Add("p|project=", "include only compilations for the given project (allows multiple)", ProjectNames.Add);
        Add("h|help", "print help", h => { if (h != null) Help = true; });

        if (analyzers)
        {
            _hasAnalyzerOptions = true;
            _basicAnalyzerKind = BasicAnalyzerHost.DefaultKind;
            Add("a|analyzers=", "analyzer load strategy: none, ondisk, inmemory", void (BasicAnalyzerKind k) => _basicAnalyzerKind = k);
            Add("n|none", "Do not run analyzers", i => { if (i is not null) _basicAnalyzerKind = BasicAnalyzerKind.None; }, hidden: true);
            Add("c|compiler=", "path to compiler to use for replay", void (string c) => _customCompilerFilePath = c);
            Add("no-strip", "do not strip ReadyToRun native code from analyzer assemblies", i => { if (i is not null) _stripReadyToRun = false; });
        }
    }

    private void CheckHasAnalyzerOptions()
    {
        if (!_hasAnalyzerOptions)
        {
            throw new InvalidOperationException();
        }
    }

    internal bool FilterCompilerCalls(CompilerCall compilerCall)
    {
        if (compilerCall.Kind != CompilerCallKind.Regular && !IncludeAllKinds)
        {
            return false;
        }

        if (TargetFrameworks.Count > 0 && !TargetFrameworks.Contains(compilerCall.TargetFramework, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ProjectNames.Count > 0)
        {
            var name = Path.GetFileName(compilerCall.ProjectFilePath);
            var nameNoExtension = Path.GetFileNameWithoutExtension(compilerCall.ProjectFilePath);
            var comparer = PathUtil.Comparer;
            return ProjectNames.Any(x => comparer.Equals(x, name) || comparer.Equals(x, nameNoExtension));
        }

        return true;
    }
}
