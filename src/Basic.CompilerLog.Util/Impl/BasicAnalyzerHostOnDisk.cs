using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
#if NETCOREAPP
using System.Runtime.Loader;
#endif
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util.Impl;

/// <summary>
/// This is a per-compilation analyzer assembly loader that can be used to produce 
/// <see cref="AnalyzerFileReference"/> instances
/// </summary>
internal sealed class BasicAnalyzerHostOnDisk : BasicAnalyzerHost
{
    internal OnDiskLoader Loader { get; }
    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore { get; }

    internal string AnalyzerDirectory { get; }

    internal BasicAnalyzerHostOnDisk(IBasicAnalyzerHostDataProvider provider, List<RawAnalyzerData> analyzers)
        : base(BasicAnalyzerKind.OnDisk)
    {
        var dirName = Guid.NewGuid().ToString("N");
        var name =  $"{nameof(BasicAnalyzerHostOnDisk)} {dirName}";
        AnalyzerDirectory = Path.Combine(provider.LogReaderState.AnalyzerDirectory, dirName);
        Directory.CreateDirectory(AnalyzerDirectory);

        Loader = new OnDiskLoader(name, AnalyzerDirectory, provider.LogReaderState);

        // Now create the AnalyzerFileReference. This won't actually pull on any assembly loading
        // until later so it can be done at the same time we're building up the files.
        var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
        foreach (var data in analyzers)
        {
            var path = Path.Combine(Loader.AnalyzerDirectory, data.FileName);
            using var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            provider.CopyAssemblyBytes(data, fileStream);
            fileStream.Dispose();

            builder.Add(new AnalyzerFileReference(path, Loader));
        }

        AnalyzerReferencesCore = builder.MoveToImmutable();
    }

    protected override void DisposeCore()
    {
        try
        {
            Directory.Delete(AnalyzerDirectory, recursive: true);
        }
        catch
        {
            // Nothing to do if we can't delete
        }
    }
}

#if NETCOREAPP

internal sealed class OnDiskLoader : AssemblyLoadContext, IAnalyzerAssemblyLoader, IDisposable
{
    internal AssemblyLoadContext CompilerLoadContext { get; set;  }
    internal string AnalyzerDirectory { get; }

    internal OnDiskLoader(string name, string analyzerDirectory, LogReaderState state)
        : base(name, isCollectible: true)
    {
        CompilerLoadContext = state.CompilerLoadContext;
        AnalyzerDirectory = analyzerDirectory;
    }

    public void Dispose()
    {
        Unload();
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        try
        {
            if (CompilerLoadContext.LoadFromAssemblyName(assemblyName) is { } compilerAssembly)
            {
                return compilerAssembly;
            }
        }
        catch
        {
            // Expected to happen when the assembly cannot be resolved in the compiler / host
            // AssemblyLoadContext.
        }

        // Prefer registered dependencies in the same directory first.
        var simpleName = assemblyName.Name!;
        var assemblyPath = Path.Combine(AnalyzerDirectory, simpleName + ".dll");
        return LoadFromAssemblyPath(assemblyPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        return IntPtr.Zero;
    }

    public Assembly LoadFromPath(string fullPath)
    {
        var name = AssemblyName.GetAssemblyName(fullPath);
        return LoadFromAssemblyName(name);
    }

    public void AddDependencyLocation(string fullPath)
    {
        // Implicitly handled already
    }
}

#else

internal sealed class OnDiskLoader : IAnalyzerAssemblyLoader, IDisposable
{
    internal string Name { get; }
    internal string AnalyzerDirectory { get; }

    internal OnDiskLoader(string name, string analyzerDirectory, LogReaderState state)
    {
        Name = name;
        AnalyzerDirectory = analyzerDirectory;

        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
    }

    private Assembly? OnAssemblyResolve(object sender, ResolveEventArgs e)
    {
        var name = Path.Combine(AnalyzerDirectory, $"{e.Name}.dll");
        if (File.Exists(name))
        {
            return Assembly.LoadFrom(name);
        }

        return null;
    }

    public Assembly LoadFromPath(string fullPath)
    {
        return Assembly.LoadFrom(fullPath);
    }

    public void AddDependencyLocation(string fullPath)
    {
        // Implicitly handled already
    }
}

#endif

