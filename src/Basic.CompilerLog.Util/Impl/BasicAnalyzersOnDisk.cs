using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util.Impl;

/// <summary>
/// This is a per-compilation analyzer assembly loader that can be used to produce 
/// <see cref="AnalyzerFileReference"/> instances
/// </summary>
internal sealed class BasicAnalyzersOnDisk : BasicAnalyzers
{
    private readonly AssemblyLoadContext _loader;

    internal string AnalyzerDirectory { get; }
    internal new ImmutableArray<AnalyzerFileReference> AnalyzerReferences { get; }

    private BasicAnalyzersOnDisk(
        AssemblyLoadContext loader,
        ImmutableArray<AnalyzerFileReference> analyzerReferences,
        string analyzerDirectory)
        : base(BasicAnalyzersOptions.OnDisk, loader, ImmutableArray<AnalyzerReference>.CastUp(analyzerReferences))
    {
        _loader = loader;
        AnalyzerDirectory = analyzerDirectory;
        AnalyzerReferences = analyzerReferences;
    }

    internal static BasicAnalyzersOnDisk Create(
        CompilerLogReader reader,
        List<RawAnalyzerData> analyzers,
        AssemblyLoadContext? compilerLoadContext = null,
        string? analyzerDirectory = null)
    {
        var name = Guid.NewGuid().ToString("N");
        analyzerDirectory ??= Path.Combine(Path.GetTempPath(), "Basic.Compiler.Logger", name);
        Directory.CreateDirectory(analyzerDirectory);

        var loader = new OnDiskLoader($"{nameof(BasicAnalyzersOnDisk)} {name}", CommonUtil.GetAssemblyLoadContext(compilerLoadContext), analyzerDirectory);

        // Now create the AnalyzerFileReference. This won't actually pull on any assembly loading
        // until later so it can be done at the same time we're building up the files.
        var builder = ImmutableArray.CreateBuilder<AnalyzerFileReference>(analyzers.Count);
        foreach (var data in analyzers)
        {
            var path = Path.Combine(analyzerDirectory, data.FileName);
            using var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            reader.CopyAssemblyBytes(data.Mvid, fileStream);
            fileStream.Dispose();

            builder.Add(new AnalyzerFileReference(path, loader));
        }

        return new BasicAnalyzersOnDisk(loader, builder.MoveToImmutable(), analyzerDirectory);
    }

    public override void DisposeCore()
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

file sealed class OnDiskLoader : AssemblyLoadContext, IAnalyzerAssemblyLoader
{
    internal AssemblyLoadContext CompilerLoadContext { get; }
    internal string AnalyzerDirectory { get; }

    internal OnDiskLoader(string name, AssemblyLoadContext compilerLoadContext, string analyzerDirectory)
        : base(name, isCollectible: true)
    {
        CompilerLoadContext = compilerLoadContext;
        AnalyzerDirectory = analyzerDirectory;
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
