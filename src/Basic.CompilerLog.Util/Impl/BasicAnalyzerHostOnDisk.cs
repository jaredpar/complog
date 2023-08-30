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

    internal string AnalyzerDirectory => Loader.AnalyzerDirectory;

    internal BasicAnalyzerHostOnDisk(CompilerLogReader reader, List<RawAnalyzerData> analyzers, BasicAnalyzerHostOptions options)
        : base(BasicAnalyzerKind.OnDisk, options)
    {
        var name =  $"{nameof(BasicAnalyzerHostOnDisk)} {Guid.NewGuid():N}";
        Loader = new OnDiskLoader(name, options);
        Directory.CreateDirectory(Loader.AnalyzerDirectory);

        // Now create the AnalyzerFileReference. This won't actually pull on any assembly loading
        // until later so it can be done at the same time we're building up the files.
        var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
        foreach (var data in analyzers)
        {
            var path = Path.Combine(Loader.AnalyzerDirectory, data.FileName);
            using var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            reader.CopyAssemblyBytes(data.Mvid, fileStream);
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

    internal OnDiskLoader(string name, BasicAnalyzerHostOptions options)
        : base(name, isCollectible: true)
    {
        CompilerLoadContext = options.CompilerLoadContext;
        AnalyzerDirectory = options.GetAnalyzerDirectory(name);
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

    internal OnDiskLoader(string name, BasicAnalyzerHostOptions options)
    {
        Name = name;
        AnalyzerDirectory = options.GetAnalyzerDirectory(name);

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

