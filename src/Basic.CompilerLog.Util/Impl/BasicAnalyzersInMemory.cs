using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.Util.Impl;

/// <summary>
/// Loads analyzers in memory
/// </summary>
internal sealed class BasicAnalyzersInMemory : BasicAnalyzers
{
    internal InMemoryLoader Loader { get; }

    private BasicAnalyzersInMemory(
        InMemoryLoader loader,
        ImmutableArray<AnalyzerReference> analyzerReferences)
        : base(BasicAnalyzersKind.InMemory, analyzerReferences)
    {
        Loader = loader;
    }

    internal static BasicAnalyzersInMemory Create(CompilerLogReader reader, List<RawAnalyzerData> analyzers, BasicAnalyzersOptions options) 
    {
        var name = $"{nameof(BasicAnalyzersInMemory)} - {Guid.NewGuid().ToString("N")}";
        var loader = new InMemoryLoader(name, options, reader, analyzers);
        return new BasicAnalyzersInMemory(loader, loader.AnalyzerReferences);
    }

    public override void DisposeCore()
    {
#if NETCOREAPP
        Loader.Unload();
#endif
    }
}

internal sealed class InMemoryLoader
#if NETCOREAPP
    : AssemblyLoadContext
#endif
{
    private readonly Dictionary<string, byte[]> _map = new();
    internal ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }

    internal InMemoryLoader(string name, BasicAnalyzersOptions options, CompilerLogReader reader, List<RawAnalyzerData> analyzers)
#if NETCOREAPP
        : base(name, isCollectible: true)
#endif
    {
#if NETCOREAPP
        CompilerLoadContext = options.CompilerLoadContext;
#endif
        var builder = ImmutableArray.CreateBuilder<AnalyzerReference>(analyzers.Count);
        foreach (var analyzer in analyzers)
        {
            var simpleName = Path.GetFileNameWithoutExtension(analyzer.FileName);
            _map[simpleName] = reader.GetAssemblyBytes(analyzer.Mvid);
            builder.Add(new BasicAnalyzerReference(new AssemblyName(simpleName), this));
        }

        AnalyzerReferences = builder.MoveToImmutable();
    }

#if NETCOREAPP

    internal AssemblyLoadContext CompilerLoadContext { get; }

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

#else

    public Assembly LoadFromAssemblyName(AssemblyName assemblyName)
    {
        var bytes = _map[assemblyName.Name];
        return Assembly.Load(bytes);
    }

#endif
}

file sealed class BasicAnalyzerReference : AnalyzerReference
{
    internal AssemblyName AssemblyName { get; }
    internal InMemoryLoader Loader { get; }
    public override object Id { get; } = Guid.NewGuid();
    public override string? FullPath => null;

    internal BasicAnalyzerReference(AssemblyName assemblyName, InMemoryLoader loader)
    {
        AssemblyName = assemblyName;
        Loader = loader;
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

    internal void GetAnalyzers(ImmutableArray<DiagnosticAnalyzer>.Builder builder, string? languageName)
    {
        var assembly = Loader.LoadFromAssemblyName(AssemblyName);
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttributes(inherit: false) is { Length: > 0 } attributes)
            {
                foreach (var attribute in attributes)
                {
                    if (attribute is DiagnosticAnalyzerAttribute d &&
                        IsLanguageMatch(d.Languages))
                    {
                        builder.Add((DiagnosticAnalyzer)CreateInstance(type));
                        break;
                    }
                }
            }
        }

        bool IsLanguageMatch(string[] languages) =>
            languageName is null || languages.Contains(languageName);
    }

    internal void GetGenerators(ImmutableArray<ISourceGenerator>.Builder builder, string? languageName)
    {
        var assembly = Loader.LoadFromAssemblyName(AssemblyName);
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttributes(inherit: false) is { Length: > 0 } attributes)
            {
                foreach (var attribute in attributes)
                {
                    if (attribute is GeneratorAttribute g &&
                        IsLanguageMatch(g.Languages))
                    {
                        var generator = CreateInstance(type);
                        if (generator is ISourceGenerator sg)
                        {
                            builder.Add(sg);
                        }
                        else
                        {
                            IIncrementalGenerator ig = (IIncrementalGenerator)generator;
                            builder.Add(ig.AsSourceGenerator());
                        }

                        break;
                    }
                }
            }
        }

        bool IsLanguageMatch(string[] languages) =>
            languageName is null || languages.Contains(languageName);
    }

    private static object CreateInstance(Type type)
    {
        var ctor = type.GetConstructors()
            .Where(c => c.GetParameters().Length == 0)
            .Single();
        return ctor.Invoke(null);
    }
}

