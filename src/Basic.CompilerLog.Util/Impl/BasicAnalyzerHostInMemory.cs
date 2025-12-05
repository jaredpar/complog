using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;


#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.Util.Impl;

/// <summary>
/// Loads analyzers in memory
/// </summary>
internal sealed class BasicAnalyzerHostInMemory : BasicAnalyzerHost
{
    internal InMemoryLoader Loader { get; private set; }
    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore => Loader.AnalyzerReferences;

    internal BasicAnalyzerHostInMemory(IBasicAnalyzerHostDataProvider provider, List<AnalyzerData> analyzers)
        :base(BasicAnalyzerKind.InMemory)
    {
        var name = $"{nameof(BasicAnalyzerHostInMemory)} - {Guid.NewGuid().ToString("N")}";
        Loader = new InMemoryLoader(name, provider, analyzers);
    }

#if NET

    /// <summary>
    /// This creates a new instance over a single analyzer.
    /// </summary>
    internal BasicAnalyzerHostInMemory(AssemblyFileData assemblyFileData)
        :base(BasicAnalyzerKind.InMemory)
    {
        var name = $"{nameof(BasicAnalyzerHostInMemory)} - {Guid.NewGuid().ToString("N")}";
        var loadContext = CommonUtil.GetAssemblyLoadContext();
        var simpleName = Path.GetFileNameWithoutExtension(assemblyFileData.FileName);
        Loader = new InMemoryLoader(name, loadContext, simpleName, assemblyFileData.Image.ToArray());
    }

#endif

    protected override void DisposeCore()
    {
        Loader.Dispose();
        Loader = null!;
    }
}

#if NET

internal sealed class InMemoryLoader : AssemblyLoadContext
{
    private readonly Dictionary<string, byte[]> _map = new();
    internal ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }
    internal AssemblyLoadContext CompilerLoadContext { get; }

    internal InMemoryLoader(string name, IBasicAnalyzerHostDataProvider provider, List<AnalyzerData> analyzers)
        :base(name, isCollectible: true)
    {
        CompilerLoadContext = provider.LogReaderState.CompilerLoadContext;
        var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
        foreach (var analyzer in analyzers)
        {
            var simpleName = Path.GetFileNameWithoutExtension(analyzer.FileName);
            var bytes = provider.GetAssemblyBytes(analyzer.AssemblyData);
            _map[simpleName] = bytes;
            builder.Add(new BasicAnalyzerReference(new AssemblyName(simpleName), bytes, this));
        }

        AnalyzerReferences = builder.MoveToImmutable();
    }

    internal InMemoryLoader(string name, AssemblyLoadContext compilerLoadContext, string simpleName, byte[] bytes)
        :base(name, isCollectible: true)
    {
        CompilerLoadContext = compilerLoadContext;
        _map[simpleName] = bytes;
        var reference = new BasicAnalyzerReference(new AssemblyName(simpleName), bytes, this);
        AnalyzerReferences = [reference];
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
        catch (FileNotFoundException)
        {
            // Expected to happen when the assembly cannot be resolved in the compiler / host
            // AssemblyLoadContext.
        }

        // Prefer registered dependencies in the same directory first.
        if (_map.TryGetValue(assemblyName.Name!, out var bytes))
        {
            return LoadFromStream(bytes.AsSimpleMemoryStream());
        }

        return null;
    }

    public void Dispose()
    {
        Unload();
    }
}

#else

internal sealed class InMemoryLoader
{
    internal ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }

    internal InMemoryLoader(string name, IBasicAnalyzerHostDataProvider provider, List<AnalyzerData> analyzers)
    {
        var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
        foreach (var analyzer in analyzers)
        {
            var bytes = provider.GetAssemblyBytes(analyzer.AssemblyData);
            builder.Add(new BasicAnalyzerReference(new AssemblyName(analyzer.AssemblyIdentityData.AssemblyName), bytes, this));
        }

        AnalyzerReferences = builder.MoveToImmutable();
    }

    public Assembly LoadFromAssemblyName(AssemblyName assemblyName)
    {
        throw new PlatformNotSupportedException();
    }

    public void Dispose()
    {
    }

    /*
     *
        rough sketch of how this could work
        private readonly Dictionary<string, (byte[], Assembly?)> _map = new();
        internal ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }

        internal InMemoryLoader(string name, BasicAnalyzersOptions options, CompilerLogReader reader, List<RawAnalyzerData> analyzers)
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
            foreach (var analyzer in analyzers)
            {
                var simpleName = Path.GetFileNameWithoutExtension(analyzer.FileName);
                _map[simpleName] = (reader.GetAssemblyBytes(analyzer.Mvid), null);
                builder.Add(new BasicAnalyzerReference(new AssemblyName(simpleName), this));
            }

            AnalyzerReferences = builder.MoveToImmutable();
        }


        private readonly HashSet<string> _loadingSet = new();

        private Assembly? LoadCore(string assemblyName)
        {
            lock (_map)
            {
                if (!_map.TryGetValue(assemblyName, out var value))
                {
                    return null;
                }

                var (bytes, assembly) = value;
                if (assembly is null)
                {
                    assembly = Assembly.Load(bytes);
                    _map[assemblyName] = (bytes, assembly);
                }

                return assembly;
            }
        }

        private Assembly? OnAssemblyResolve(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name);
            if (LoadCore(name.Name) is { } assembly)
            {
                return assembly;
            }

            lock (_loadingSet)
            {
                if (!_loadingSet.Add(e.Name))
                {
                    return null;
                }
            }

            try
            {
                return AppDomain.CurrentDomain.Load(name);
            }
            finally
            {
                lock (_loadingSet)
                {
                    _loadingSet.Remove(e.Name);
                }
            }
        }

        public Assembly LoadFromAssemblyName(AssemblyName assemblyName) =>
            LoadCore(assemblyName.Name) ?? throw new Exception($"Cannot find assembly with name {assemblyName.Name}");

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
        }

        */
}

#endif

file sealed class BasicAnalyzerReference : AnalyzerReference, IBasicAnalyzerReference
{
    internal AssemblyName AssemblyName { get; }
    internal byte[] AssemblyBytes { get; }
    internal InMemoryLoader Loader { get; }
    public override object Id { get; } = Guid.NewGuid();
    public override string? FullPath => null;
    public override string Display => AssemblyName.Name ?? "";

    internal BasicAnalyzerReference(AssemblyName assemblyName, byte[] assemblyBytes, InMemoryLoader loader)
    {
        AssemblyName = assemblyName;
        AssemblyBytes = assemblyBytes;
        Loader = loader;
    }

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) =>
        GetAnalyzers(language, diagnostics: null);

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() =>
        GetAnalyzers(language: null, diagnostics: null);

    public override ImmutableArray<ISourceGenerator> GetGenerators(string language) =>
        GetGenerators(language, diagnostics: null);

    public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() =>
        GetGenerators(language: null, diagnostics: null);

    internal ImmutableArray<T> GetAnalyzersCore<T>(
        Action<Assembly, MetadataReader, ImmutableArray<T>.Builder, string?, List<Diagnostic>?> action,
        string? language,
        List<Diagnostic>? diagnostics)
    {
        try
        {
            var builder = ImmutableArray.CreateBuilder<T>();
            var assembly = Loader.LoadFromAssemblyName(AssemblyName);
            using var peReader = new PEReader(new MemoryStream(AssemblyBytes));
            var metadataReader = peReader.GetMetadataReader();
            action(assembly, metadataReader, builder, language, diagnostics);
            return builder.ToImmutable();
        }
        catch (Exception ex)
        {
            if (diagnostics is not null)
            {
                var d = Diagnostic.Create(
                    RoslynUtil.CannotLoadTypesDiagnosticDescriptor,
                    Location.None,
                    $"{AssemblyName.Name}:{ex.Message}");
                diagnostics.Add(d);
            }

            return ImmutableArray<T>.Empty;
        }
    }

    public ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string? language, List<Diagnostic>? diagnostics) =>
        GetAnalyzersCore<DiagnosticAnalyzer>(static (assembly, metadataReader, builder, language, diagnostics) =>
        {
            foreach (var (typeDef, attribute) in RoslynUtil.GetAnalyzerTypeDefinitions(metadataReader, language))
            {
                var fqn = RoslynUtil.GetFullyQualifiedName(metadataReader, typeDef);
                var type = GetTypeWithDiagnostics(assembly, fqn, diagnostics);
                if (type is not null)
                {
                    // When looking for "all languages" roslyn will include duplicates for all
                    // supported languages. This is undocumented behavior that we need to mimic
                    //
                    // https://github.com/dotnet/roslyn/blob/329bb90e91561c8f26e4f8aeae17be1697db850b/src/Compilers/Core/Portable/DiagnosticAnalyzer/AnalyzerFileReference.cs#L111
                    var count = language is not null
                        ? 1
                        : RoslynUtil.CountLanguageNames(metadataReader, attribute);
                    for (int i = 0; i < count; i++)
                    {
                        if (Activator.CreateInstance(type) is DiagnosticAnalyzer d)
                        {
                            builder.Add(d);
                        }
                    }
                }
            }
        }, language, diagnostics);

    public ImmutableArray<ISourceGenerator> GetGenerators(string? language, List<Diagnostic>? diagnostics) =>
        GetAnalyzersCore<ISourceGenerator>(static (assembly, metadataReader, builder, language, diagnostics) =>
        {
            foreach (var (typeDef, attribute) in RoslynUtil.GetGeneratorTypeDefinitions(metadataReader, language))
            {
                var fqn = RoslynUtil.GetFullyQualifiedName(metadataReader, typeDef);
                var type = GetTypeWithDiagnostics(assembly, fqn, diagnostics);
                if (type is not null)
                {
                    var generator = Activator.CreateInstance(type);
                    if (generator is ISourceGenerator sg)
                    {
                        builder.Add(sg);
                    }
                    else if (generator is IIncrementalGenerator ig)
                    {
                        builder.Add(ig.AsSourceGenerator());
                    }
                }
            }
        }, language, diagnostics);

    private static Type? GetTypeWithDiagnostics(Assembly assembly, string fqn, List<Diagnostic>? diagnostics)
    {
        try
        {
            return assembly.GetType(fqn, throwOnError: true);
        }
        catch (Exception ex)
        {
            if (diagnostics is not null)
            {
                var args = new AnalyzerLoadFailureEventArgs(
                    AnalyzerLoadFailureEventArgs.FailureErrorCode.UnableToCreateAnalyzer,
                    $"Unable to load analyzer {fqn} from ({assembly.FullName})",
                    exceptionOpt: ex,
                    typeNameOpt: fqn);
                var d = Diagnostic.Create(
                    RoslynUtil.CannotLoadTypesDiagnosticDescriptor,
                    Location.None,
                    $"{args.TypeName}:{args.Exception?.Message}");
                diagnostics.Add(d);
            }
        }

        return null;
    }

    [ExcludeFromCodeCoverage]
    public override string ToString() => $"In Memory {AssemblyName}";
}

