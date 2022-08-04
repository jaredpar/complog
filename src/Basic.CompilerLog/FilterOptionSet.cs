using Basic.CompilerLog.Util;
using Mono.Options;

internal sealed class FilterOptionSet : OptionSet
{
    internal List<string> TargetFrameworks { get; } = new();
    internal bool IncludeSatelliteAssemblies { get; set; }
    internal string? ProjectName { get; set; }
    internal bool Help { get; set; }

    internal FilterOptionSet()
    {
        Add("s|satellite", "include satellite asseblies", s => { if (s != null) IncludeSatelliteAssemblies = true; });
        Add("targetframework=", "include only compilations for the target framework (allows multiple)", TargetFrameworks.Add);
        Add("n|projectName=", "include only compilations with the project name", (string n) => ProjectName = n);
        Add("h|help", "print help", h => { if (h != null) Help = true; });
    }

    internal bool FilterCompilerCalls(CompilerCall compilerCall)
    {
        if (!IncludeSatelliteAssemblies && compilerCall.Kind == CompilerCallKind.Satellite)
        {
            return false;
        }

        if (TargetFrameworks.Count > 0 && !TargetFrameworks.Contains(compilerCall.TargetFramework, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(ProjectName))
        {
            var comparer = StringComparer.OrdinalIgnoreCase;
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
