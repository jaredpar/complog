using System.Reflection;
using System.Runtime.Loader;

internal sealed class CompilerAssemblyLoadContext : AssemblyLoadContext
{
    internal string ToolDirectory { get;}
    internal string CompilerDirectory { get;}

    public CompilerAssemblyLoadContext(string toolDirectory, string compilerDirectory)
        : base("CompilerLoadContext", isCollectible: true)
    {
        ToolDirectory = toolDirectory;
        CompilerDirectory = compilerDirectory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var fileName = $"{assemblyName.Name}.dll";
        var filePath = Path.Combine(CompilerDirectory, fileName);
        if (File.Exists(filePath))
        {
            return LoadFromAssemblyPath(filePath);
        }

        filePath = Path.Combine(ToolDirectory, fileName);
        if (File.Exists(filePath))
        {
            return LoadFromAssemblyPath(filePath);
        }

        return null;
    }
}