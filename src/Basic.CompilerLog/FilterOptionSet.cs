using Basic.CompilerLog.Util;
using Mono.Options;

internal sealed class FilterOptionSet : OptionSet
{
    internal List<string> TargetFrameworks { get; } = new();
    internal bool IncludeAllKinds { get; set; }
    internal List<string> ProjectNames { get; } = new();
    internal bool Help { get; set; }
    internal bool UseNoneHost { get; set; }

    internal FilterOptionSet(bool includeNoneHost = false)
    {
        Add("include", "include all compilation kinds", i => { if (i is not null) IncludeAllKinds = true; });
        Add("targetframework=", "", TargetFrameworks.Add, hidden: true);
        Add("f|framework=", "include only compilations for the target framework (allows multiple)", TargetFrameworks.Add);
        Add("p|project=", "include only compilations for the given project (allows multiple)", ProjectNames.Add);
        Add("n|projectName=", "include only compilations for the project", ProjectNames.Add, hidden: true);
        Add("h|help", "print help", h => { if (h != null) Help = true; });

        if (includeNoneHost)
        {
            Add("none", "do not use original analyzers / generators", n => {  if (n != null) UseNoneHost = true; });
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
