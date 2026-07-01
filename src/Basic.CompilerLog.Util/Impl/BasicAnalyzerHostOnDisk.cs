using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.VisualBasic;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
    private OnDiskLoader Loader { get; }
    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore { get; }

    private BasicAnalyzerHostOnDisk(LogReaderState state)
        : base(BasicAnalyzerKind.OnDisk)
    {
        Loader = new OnDiskLoader(state);
    }

    internal BasicAnalyzerHostOnDisk(IBasicAnalyzerHostDataProvider provider, List<AnalyzerData> analyzers)
        : this(provider.LogReaderState)
    {
        // Now create the AnalyzerFileReference. This won't actually pull on any assembly loading
        // until later so it can be done at the same time we're building up the files.
        var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
        foreach (var data in analyzers)
        {
            var path = Path.Combine(Loader.LoaderDirectory, data.FileName);
            using var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            provider.CopyAnalyzerBytes(data, fileStream);
            fileStream.Dispose();

            builder.Add(new AnalyzerFileReference(path, this));
        }

        AnalyzerReferencesCore = builder.MoveToImmutable();
    }

    internal BasicAnalyzerHostOnDisk(LogReaderState state, AssemblyFileData assemblyFileData)
        : this(state)
    {
        var filePath = Path.Combine(Loader.LoaderDirectory, assemblyFileData.FileName);
        File.WriteAllBytes(filePath, assemblyFileData.Image.ToArray());
        AnalyzerReferencesCore = [new AnalyzerFileReference(filePath, this)];
    }

    protected override void DisposeCore()
    {
        Loader.Dispose();
    }

    Assembly IAnalyzerAssemblyLoader.LoadFromPath(string fullPath) =>
        Loader.LoadFromPath(fullPath);

    void IAnalyzerAssemblyLoader.AddDependencyLocation(string fullPath)
    {
    }
}

#if NET

internal sealed class OnDiskLoader : IDisposable
{
    private static int _activeAssemblyLoadContextCount = 0;

    /// <summary>
    /// When an <see cref="AssemblyLoadContext"/> is unloaded it cleans up asynchronously. Use a CWT
    /// here so that when the context is collected we can come back around and clean up the directory
    /// where the files were written.
    ///
    /// This cleanup is intentionally <em>optimistic</em>: it is driven by the finalizer of
    /// <see cref="AnalyzerDirectoryCleanup"/> which only runs once the associated
    /// <see cref="AssemblyLoadContext"/> is actually collected. There is no guarantee that a
    /// collectible <see cref="AssemblyLoadContext"/> is ever collected (the runtime keeps them alive
    /// while any reference, including transitive ones held by loaded analyzers, remains). When that
    /// happens the finalizer never runs and the on disk directories are left behind. That is an
    /// accepted trade off: the files live under a temp directory and callers should not depend on
    /// this cleanup completing.
    /// </summary>
    private static ConditionalWeakTable<OnDiskLoadContext, AnalyzerDirectoryCleanup> AnalyzerDirectoryCleanupMap { get; } = new();

    private sealed class AnalyzerDirectoryCleanup(LogReaderState state, string loaderDirectory) : IDisposable
    {
        private (string BaseDirectory, string AnalyzerDirectory) Tuple { get; } = (state.BaseDirectory, state.AnalyzerDirectory);
        private string LoaderDirectory { get; } = loaderDirectory;

        ~AnalyzerDirectoryCleanup()
        {
            Dispose();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(LoaderDirectory, recursive: true);

                // It's not deterministic if the LogReaderState will be disposed before or after this. To ensure
                // a clean directory state both components should attempt to clean up the directories.
                CommonUtil.DeleteDirectoryIfEmpty(Tuple.AnalyzerDirectory);
                CommonUtil.DeleteDirectoryIfEmpty(Tuple.BaseDirectory);
            }
            catch
            {
                // Nothing to do if we can't delete
            }

            Interlocked.Decrement(ref _activeAssemblyLoadContextCount);
        }
    }

    private sealed class OnDiskLoadContext(string name, AssemblyLoadContext compilerLoadContext, string analyzerDirectory) 
        : AssemblyLoadContext(name, isCollectible: true)
    {
        internal AssemblyLoadContext CompilerLoadContext { get; } = compilerLoadContext;
        internal string AnalyzerDirectory { get; } = analyzerDirectory;

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
    }

    /// <summary>
    /// This is a test only helper used to decide whether a best effort GC / finalization pass is
    /// worth running to clean up on disk analyzer directories. It reflects process-global state, so
    /// callers must treat it only as an optimization hint and must not use it to verify or force
    /// cleanup while other work may be creating contexts concurrently.
    /// </summary>
    internal static bool AnyActiveAssemblyLoadContext => Volatile.Read(ref _activeAssemblyLoadContextCount) > 0;

    private OnDiskLoadContext LoadContext { get; set; }
    internal AssemblyLoadContext CompilerLoadContext { get; }
    internal string LoaderDirectory { get; }

    internal OnDiskLoader(LogReaderState state)
    {
        var dirName = Guid.NewGuid().ToString("N");
        CompilerLoadContext = state.CompilerLoadContext;
        LoaderDirectory = Path.Combine(state.AnalyzerDirectory, dirName);
        _ = Directory.CreateDirectory(LoaderDirectory);
        LoadContext = new($"{nameof(OnDiskLoadContext)} {dirName}", CompilerLoadContext, LoaderDirectory);
        Interlocked.Increment(ref _activeAssemblyLoadContextCount);
        AnalyzerDirectoryCleanupMap.Add(LoadContext, new(state, LoaderDirectory));
    }

    public void Dispose()
    {
        LoadContext.Unload();
        LoadContext = null!;
    }

    public Assembly LoadFromPath(string fullPath)
    {
        var name = AssemblyName.GetAssemblyName(fullPath);
        return LoadContext.LoadFromAssemblyName(name);
    }
}

#else

internal sealed class OnDiskLoader : IDisposable
{
    internal string Name { get; }
    internal string LoaderDirectory { get; }
    internal Dictionary<string, Assembly> AssemblyMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal OnDiskLoader(LogReaderState state)
    {
        Name = Guid.NewGuid().ToString("N");
        LoaderDirectory = Path.Combine(state.AnalyzerDirectory, Name);
        _ = Directory.CreateDirectory(LoaderDirectory);

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

        var name = Path.Combine(LoaderDirectory, $"{e.Name}.dll");
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

