using Basic.CompilerLog.Util;
using Mono.Options;

internal sealed class FilterOptionSet : OptionSet
{
    internal List<string> TargetFrameworks { get; } = new();
    internal bool IncludeSatelliteAssemblies { get; set; }

    internal FilterOptionSet()
    {
        Add("s|satellite", "include satellite asseblies", s => { if (s != null) IncludeSatelliteAssemblies = true; });
        Add("targetframework", "include only compilations for the target framework (allows multiple)", TargetFrameworks.Add);
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

        return true;
    }
}
