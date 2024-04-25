using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Options;

internal sealed class FilterOptionSet : OptionSet
{
    private bool _hasAnalyzerOptions;
    private BasicAnalyzerKind _basicAnalyzerKind;

    internal List<string> TargetFrameworks { get; } = new();
    internal bool IncludeAllKinds { get; set; }
    internal List<string> ProjectNames { get; } = new();
    internal bool Help { get; set; }

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

    internal FilterOptionSet(bool analyzers = false)
    {
        Add("include", "include all compilation kinds", i => { if (i is not null) IncludeAllKinds = true; });
        Add("f|framework=", "include only compilations for the target framework (allows multiple)", TargetFrameworks.Add);
        Add("p|project=", "include only compilations for the given project (allows multiple)", ProjectNames.Add);
        Add("n|projectName=", "include only compilations for the project", ProjectNames.Add, hidden: true);
        Add("h|help", "print help", h => { if (h != null) Help = true; });

        if (analyzers)
        {
            _hasAnalyzerOptions = true;
            _basicAnalyzerKind = BasicAnalyzerHost.DefaultKind;
            Add("a|analyzers=", "analyzer load strategy: none, ondisk, inmemory", void (BasicAnalyzerKind k) => _basicAnalyzerKind = k);
            Add("none", "Do not run analyzers", i => { if (i is not null) _basicAnalyzerKind = BasicAnalyzerKind.None; }, hidden: true);
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
