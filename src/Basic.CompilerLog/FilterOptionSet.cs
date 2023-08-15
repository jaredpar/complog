using Basic.CompilerLog.Util;
using Mono.Options;

internal sealed class FilterOptionSet : OptionSet
{
    internal List<string> TargetFrameworks { get; } = new();
    internal bool IncludeAllKinds { get; set; }
    internal string? ProjectName { get; set; }
    internal bool Help { get; set; }
    internal bool UseNoneHost { get; set; }

    internal FilterOptionSet(bool includeNoneHost = false)
    {
        Add("include", "include all compilation kinds", i => { if (i != null) IncludeAllKinds = true; });
        Add("targetframework=", "", TargetFrameworks.Add, hidden: true);
        Add("framework=", "include only compilations for the target framework (allows multiple)", TargetFrameworks.Add);
        Add("n|projectName=", "include only compilations with the project name", (string n) => ProjectName = n);
        Add("h|help", "print help", h => { if (h != null) Help = true; });

        if (includeNoneHost)
        {
            Add("none", "do not use original analyzers / generators", n => {  if (n != null) UseNoneHost = true; });
        }
    }

    internal BasicAnalyzerHostOptions? CreateHostOptions() =>
        UseNoneHost ? BasicAnalyzerHostOptions.None : null;

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

        if (!string.IsNullOrEmpty(ProjectName))
        {
            var comparer = PathUtil.Comparer;
            if (comparer.Equals(ProjectName, Path.GetFileName(compilerCall.ProjectFilePath)) ||
                comparer.Equals(ProjectName, Path.GetFileNameWithoutExtension(compilerCall.ProjectFilePath)))
            {
                return true;
            }

            return false;
        }

        return true;
    }
}
