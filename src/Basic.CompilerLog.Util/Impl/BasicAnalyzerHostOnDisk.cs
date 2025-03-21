using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.VisualBasic;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;


#if NET
using System.Runtime.Loader;
#endif
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util.Impl;

/// <summary>
/// This is a per-compilation analyzer assembly loader that can be used to produce 
/// <see cref="AnalyzerFileReference"/> instances
/// </summary>
internal sealed class BasicAnalyzerHostOnDisk : BasicAnalyzerHost, IAnalyzerAssemblyLoader
{
    private OnDiskLoader Loader { get; set; }
    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore { get; }

    internal string AnalyzerDirectory => Loader.AnalyzerDirectory;

    private BasicAnalyzerHostOnDisk(LogReaderState state)
        : base(BasicAnalyzerKind.OnDisk)
    {
        var dirName = Guid.NewGuid().ToString("N");
        var name = $"{nameof(BasicAnalyzerHostOnDisk)} {dirName}";
        Loader = new OnDiskLoader(name, Path.Combine(state.AnalyzerDirectory, dirName), state);
    }

    internal BasicAnalyzerHostOnDisk(IBasicAnalyzerHostDataProvider provider, List<AnalyzerData> analyzers)
        : this(provider.LogReaderState)
    {
        // Now create the AnalyzerFileReference. This won't actually pull on any assembly loading
        // until later so it can be done at the same time we're building up the files.
        var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
        foreach (var data in analyzers)
        {
            var path = Path.Combine(Loader.AnalyzerDirectory, data.FileName);
            using var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            provider.CopyAssemblyBytes(data.AssemblyData, fileStream);
            fileStream.Dispose();

            builder.Add(new AnalyzerFileReference(path, this));
        }

        AnalyzerReferencesCore = builder.MoveToImmutable();
    }

    internal BasicAnalyzerHostOnDisk(LogReaderState state, AssemblyFileData assemblyFileData)
        : this(state)
    {
        var filePath = Path.Combine(AnalyzerDirectory, assemblyFileData.FileName);
        File.WriteAllBytes(filePath, assemblyFileData.Image.ToArray());
        AnalyzerReferencesCore = [new AnalyzerFileReference(filePath, this)];
    }

    protected override void DisposeCore()
    {
        Loader.Dispose();
        Loader = null!;
    }

    Assembly IAnalyzerAssemblyLoader.LoadFromPath(string fullPath) =>
        Loader.LoadFromPath(fullPath);

    void IAnalyzerAssemblyLoader.AddDependencyLocation(string fullPath)
    {
    }
}

#if NET

internal sealed class OnDiskLoader : AssemblyLoadContext, IDisposable
{
    private static int _activeAssemblyLoadContextCount = 0;

    /// <summary>
    /// When an <see cref="AssemblyLoadContext"/> is unloaded it cleans up asynchronously. Use a CWT
    /// here so that when the context is collected we can come back around and clean up the directory
    /// where the files were written.
    /// </summary>
    private static ConditionalWeakTable<AssemblyLoadContext, AnalyzerDirectoryCleanup> AnalyzerDirectoryCleanupMap { get; } = new();

    private sealed class AnalyzerDirectoryCleanup(LogReaderState state, string analyzerDirectory) : IDisposable
    {
        private LogReaderState State { get; } = state;
        private string AnalyzerDirectory { get; } = analyzerDirectory;

        ~AnalyzerDirectoryCleanup()
        {
            Dispose();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(AnalyzerDirectory, recursive: true);

                if (State.IsDisposed)
                {
                    CommonUtil.DeleteDirectoryIfEmpty(State.AnalyzerDirectory);
                    CommonUtil.DeleteDirectoryIfEmpty(State.BaseDirectory);
                }
            }
            catch
            {
                // Nothing to do if we can't delete
            }

            Interlocked.Decrement(ref _activeAssemblyLoadContextCount);
        }
    }

    /// <summary>
    /// This is a test only helper to determine if there are any active <see cref="AssemblyLoadContext"/> 
    /// instances. This way the test can setup a GC loop if needed to verify cleanup is happening
    /// as expected.
    /// </summary>
    internal static bool AnyActiveAssemblyLoadContext => _activeAssemblyLoadContextCount > 0;

    internal AssemblyLoadContext CompilerLoadContext { get; set;  }
    internal string AnalyzerDirectory { get; }

    internal OnDiskLoader(string name, string analyzerDirectory, LogReaderState state)
        : base(name, isCollectible: true)
    {
        CompilerLoadContext = state.CompilerLoadContext;
        AnalyzerDirectory = analyzerDirectory;
        _ = Directory.CreateDirectory(analyzerDirectory);
        Interlocked.Increment(ref _activeAssemblyLoadContextCount);
        AnalyzerDirectoryCleanupMap.Add(this, new(state, analyzerDirectory));
    }

    public void Dispose()
    {
        Unload();

        // Clear out this map which roots this instance and prevents it from being collected and 
        // allowing us to clean up the directory.
        RoslynUtil.ClearLocalizableStringMap();
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
}

#else

internal sealed class OnDiskLoader : IDisposable
{
    internal string Name { get; }
    internal string AnalyzerDirectory { get; }
    internal Dictionary<string, Assembly> AssemblyMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal OnDiskLoader(string name, string analyzerDirectory, LogReaderState state)
    {
        Name = name;
        AnalyzerDirectory = analyzerDirectory;

        _ = Directory.CreateDirectory(analyzerDirectory);

        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

        // When running as a library there is no guarantee that we have a app.config with correct
        // binding redirects. To account for this we will map the core compiler assemblies by 
        // simple name and resolve them in assembly resolve.
        Assembly[] platformAssemblies =
        [
            typeof(Compilation).Assembly,
            typeof(CSharpSyntaxNode).Assembly,
            typeof(VisualBasicSyntaxNode).Assembly,
            typeof(ImmutableArray).Assembly,
            typeof(PEReader).Assembly,
        ];

        foreach (var assembly in platformAssemblies)
        {
            AssemblyMap[assembly.GetName().Name] = assembly;
        }
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
    }

    private Assembly? OnAssemblyResolve(object sender, ResolveEventArgs e)
    {
        var assemblyName = new AssemblyName(e.Name);
        if (AssemblyMap.TryGetValue(assemblyName.Name, out var assembly))
        {
            return assembly;
        }

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
}

#endif

