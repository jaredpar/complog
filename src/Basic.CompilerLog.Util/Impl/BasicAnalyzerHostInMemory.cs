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
    internal InMemoryLoader Loader { get; }
    protected override ImmutableArray<AnalyzerReference> AnalyzerReferencesCore => Loader.AnalyzerReferences;

    internal BasicAnalyzerHostInMemory(IBasicAnalyzerHostDataProvider provider, List<AnalyzerData> analyzers)
        :base(BasicAnalyzerKind.InMemory)
    {
        var name = $"{nameof(BasicAnalyzerHostInMemory)} - {Guid.NewGuid().ToString("N")}";
        Loader = new InMemoryLoader(name, provider, analyzers, AddDiagnostic);
    }

    protected override void DisposeCore()
    {
        Loader.Dispose();
    }
}

#if NET

internal sealed class InMemoryLoader : AssemblyLoadContext
{
    private readonly Dictionary<string, byte[]> _map = new();
    internal ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }
    internal AssemblyLoadContext CompilerLoadContext { get; }

    internal InMemoryLoader(string name, IBasicAnalyzerHostDataProvider provider, List<AnalyzerData> analyzers, Action<Diagnostic> onDiagnostic)
        :base(name, isCollectible: true)
    {
        CompilerLoadContext = provider.LogReaderState.CompilerLoadContext;
        var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
        foreach (var analyzer in analyzers)
        {
            var simpleName = Path.GetFileNameWithoutExtension(analyzer.FileName);
            var bytes = provider.GetAssemblyBytes(analyzer.AssemblyData);
            _map[simpleName] = bytes;
            builder.Add(new BasicAnalyzerReference(new AssemblyName(simpleName), bytes, this, onDiagnostic));
        }

        AnalyzerReferences = builder.MoveToImmutable();
    }

    internal InMemoryLoader(string name, AssemblyLoadContext compilerLoadContext, string simpleName, byte[] bytes, Action<Diagnostic> onDiagnostic)
        :base(name, isCollectible: true)
    {
        CompilerLoadContext = compilerLoadContext;
        _map[simpleName] = bytes;
        var reference = new BasicAnalyzerReference(new AssemblyName(simpleName), bytes, this, onDiagnostic);
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
        catch
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

    internal InMemoryLoader(string name, IBasicAnalyzerHostDataProvider provider, List<AnalyzerData> analyzers, Action<Diagnostic> onDiagnostic)
    {
        var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
        foreach (var analyzer in analyzers)
        {
            var bytes = provider.GetAssemblyBytes(analyzer.AssemblyData);
            builder.Add(new BasicAnalyzerReference(new AssemblyName(analyzer.AssemblyIdentityData.AssemblyName), bytes, this, onDiagnostic));
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

file sealed class BasicAnalyzerReference : AnalyzerReference
{
    public static readonly DiagnosticDescriptor CannotLoadTypes =
        new DiagnosticDescriptor(
            "BCLA0002",
            "Failed to load types from assembly",
            "Failed to load types from {0}: {1}",
            "BasicCompilerLog",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

    internal AssemblyName AssemblyName { get; }
    internal byte[] AssemblyBytes { get; }
    internal InMemoryLoader Loader { get; }
    internal Action<Diagnostic> OnDiagnostic { get; }
    public override object Id { get; } = Guid.NewGuid();
    public override string? FullPath => null;
    public override string Display => AssemblyName.Name ?? "";

    internal BasicAnalyzerReference(AssemblyName assemblyName, byte[] assemblyBytes, InMemoryLoader loader, Action<Diagnostic> onDiagnostic)
    {
        AssemblyName = assemblyName;
        AssemblyBytes = assemblyBytes;
        Loader = loader;
        OnDiagnostic = onDiagnostic;
    }

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language)
    {
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        GetAnalyzers(builder, language);
        return builder.ToImmutable();
    }

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages()
    {
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        GetAnalyzers(builder, languageName: null);
        return builder.ToImmutable();
    }

    public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
    {
        var builder = ImmutableArray.CreateBuilder<ISourceGenerator>();
        GetGenerators(builder, language);
        return builder.ToImmutable();
    }

    public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages()
    {
        var builder = ImmutableArray.CreateBuilder<ISourceGenerator>();
        GetGenerators(builder, languageName: null);
        return builder.ToImmutable();
    }

    internal List<Type> GetTypes(
        string attributeNamespace,
        string attributeName,
        string? languageName,
        Action<string?, Assembly, List<Type>, MetadataReader, TypeDefinition, CustomAttribute> action)
    {
        try
        {
            var assembly = Loader.LoadFromAssemblyName(AssemblyName);
            using var peReader = new PEReader(new MemoryStream(AssemblyBytes));
            var list = new List<Type>();
            var metadataReader = peReader.GetMetadataReader();
            RoslynUtil.ForEachTypeWithAttribute(
                metadataReader,
                attributeNamespace,
                attributeName,
                (typeDef, attribute) => action(languageName, assembly, list, metadataReader, typeDef, attribute));

            return list;
        }
        catch (Exception ex)
        {
            var diagnostic = Diagnostic.Create(CannotLoadTypes, Location.None, AssemblyName.FullName, ex.Message);
            OnDiagnostic(diagnostic);
            return [];
        }
    }

    internal void GetAnalyzers(ImmutableArray<DiagnosticAnalyzer>.Builder builder, string? languageName)
    {
        var attributeType = typeof(DiagnosticAnalyzerAttribute);
        foreach (var type in GetTypes(attributeType.Namespace!, attributeType.Name, languageName, CoreAction))
        {
            if (Activator.CreateInstance(type) is DiagnosticAnalyzer d)
            {
                builder.Add(d);
            }
        }

        static void CoreAction(
            string? languageName,
            Assembly assembly,
            List<Type> list,
            MetadataReader metadataReader,
            TypeDefinition typeDef,
            CustomAttribute attribute)
        {
            if (languageName is null || RoslynUtil.IsLanguageName(metadataReader, attribute, languageName))
            {
                var fqn = RoslynUtil.GetFullyQualifiedName(metadataReader, typeDef);
                var type = assembly.GetType(fqn, throwOnError: true);
                if (type is not null)
                {
                    // When looking for "all languages" roslyn will include duplicates for all 
                    // supported languages. This is undocumented behavior that we need to mimic
                    //
                    // https://github.com/dotnet/roslyn/blob/329bb90e91561c8f26e4f8aeae17be1697db850b/src/Compilers/Core/Portable/DiagnosticAnalyzer/AnalyzerFileReference.cs#L111
                    var count = languageName is not null
                        ? 1
                        : RoslynUtil.CountLanguageNames(metadataReader, attribute);
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(type);
                    }
                }
            }
        }
    }

    internal void GetGenerators(ImmutableArray<ISourceGenerator>.Builder builder, string? languageName)
    {
        var attributeType = typeof(GeneratorAttribute);
        foreach (var type in GetTypes(attributeType.Namespace!, attributeType.Name, languageName, CoreAction))
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

        static void CoreAction(
            string? languageName,
            Assembly assembly,
            List<Type> list,
            MetadataReader metadataReader,
            TypeDefinition typeDef,
            CustomAttribute attribute)
        {
            var match = false;
            if (languageName is null)
            {
                match = true;
            }
            else if (RoslynUtil.IsEmptyAttribute(metadataReader, attribute))
            {
                // The empty attribute is an implicit C# 
                match = languageName == LanguageNames.CSharp;
            }
            else
            {
                match = RoslynUtil.IsLanguageName(metadataReader, attribute, languageName);
            }

            if (match)
            {
                var fqn = RoslynUtil.GetFullyQualifiedName(metadataReader, typeDef);
                var type = assembly.GetType(fqn, throwOnError: false);
                if (type is not null)
                {
                    list.Add(type);
                }
            }
        }
    }

    public override string ToString() => $"In Memory {AssemblyName}";
}

