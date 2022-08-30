using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util.Impl;

internal sealed class BasicAnalyzerReference : AnalyzerReference
{
    internal Assembly Assembly { get; }
    public override object Id { get; } = Guid.NewGuid();
    public override string? FullPath => null;

    internal BasicAnalyzerReference(Assembly assembly)
    {
        Assembly = assembly;
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
        foreach (var type in Assembly.GetTypes())
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
        foreach (var type in Assembly.GetTypes())
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
