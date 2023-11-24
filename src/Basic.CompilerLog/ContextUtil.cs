

using System.Reflection;
using System.Runtime.Loader;
using Basic.CompilerLog.Util;

internal sealed class ContextUtil 
{
    private readonly CompilerLoadContext _context = new ();

    internal int RunInStream(Stream stream, IEnumerable<string> args)
    {
        var assembly = _context.LoadFromAssemblyName(typeof(ContextUtil).Assembly.GetName());
        var type = assembly.GetType("Program");
        var methods = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).ToArray();
        var method = methods.Single(x => x.Name.Contains("RunReplayCore"));
        method.Invoke(null, new object[] { args, false });
        return 0;
    }

    private static void RunCore(Stream stream, IEnumerable<string> args, Action<Stream, IEnumerable<string>> action)
    {
        action(stream, args);
    }
}

/// <summary>
/// This <see cref="AssemblyLoadContext"/> is used to load compiler assemblies
/// that may not be part of the 
/// </summary>
internal sealed class CompilerLoadContext : AssemblyLoadContext
{
    private const string CompilerDirectory = @"C:\program files\dotnet\sdk\8.0.100\Roslyn\bincore";
    private readonly string _appDirectory;

    internal CompilerLoadContext() : base("CompilerLoadContext")
    {
        _appDirectory = Path.GetDirectoryName(typeof(CompilerLoadContext).Assembly.Location);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = $"{assemblyName.Name}.dll";
        foreach (var dir in new string[] {CompilerDirectory, _appDirectory })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
            {
                return LoadFromAssemblyPath(path);
            }
        }

        return null;
    }
}