using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using Basic.CompilerLog.Util;

namespace Basic.CompilerLog.App;

internal sealed class CustomCompilerLoadContext : AssemblyLoadContext
{
    internal Dictionary<string, Version?> CompilerAssemblyMap { get; } = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Create an <see cref="AssemblyLoadContext"/> that loads assemblies from the given directories. The order
    /// of the directories matters as matches in earlier directories take precedence over later ones.
    /// </summary>
    internal CustomCompilerLoadContext(string[] dllDirectories)
        : base("Custom Compiler Load Context")
    {
        var hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dllDirectories)
        {
            foreach (var dllFilePath in Directory.EnumerateFiles(dir, "*.dll"))
            {
                // There are native .dll files in the compiler directory, check to see if it's a .NET assembly
                // by reading the MVID.
                if (RoslynUtil.TryReadMvid(dllFilePath) is { })
                {
                    var name = MetadataReader.GetAssemblyName(dllFilePath);
                    if (!CompilerAssemblyMap.ContainsKey(name.Name!))
                    {
                        CompilerAssemblyMap[name.Name!] = name.Version;
                        LoadFromAssemblyPath(dllFilePath);
                    }
                }
            }
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The .NET runtime will not consider an assembly request for a higher version to be satisfied by a lower version.
        // That means without this check it will silently fall back to the default ALC which possibly has a version that
        // satisfies the version requirement. That leads to incredibly hard to diagnose issues because the analyzer logic
        // effectively has two versions of types like DiagnosticAnalyzer.
        //
        // This type is meant for loading newer compilers, not older ones. This is a case that needs to be proactively
        // blocked. Just as the command line compiler would proactively block this situation.
        if (assemblyName.Version is { } requestedVersion &&
            CompilerAssemblyMap.TryGetValue(assemblyName.Name!, out var loadedVersion) &&
            requestedVersion > loadedVersion)
        {
            throw new Exception("The requested version of compiler assembly {assemblyName.Name} is greater than the version loaded from the compiler directory.");
        }

        return null;
    }
}
