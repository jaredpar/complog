using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Basic.CompilerLog.Util.Impl;

/// <summary>
/// This is used to load the analyzers from the compiler log file
///
/// TODO: consider handling the nested load context scenarios
/// </summary>
internal sealed class BasicAssemblyLoadContext : AssemblyLoadContext
{
    internal ImmutableArray<BasicAnalyzerReference> AnalyzerReferences { get; }

    internal BasicAssemblyLoadContext(string name, CompilerLogReader reader, List<RawAnalyzerData> analyzers)
    {
        var builder = ImmutableArray.CreateBuilder<BasicAnalyzerReference>(analyzers.Count);
        foreach (var analyzer in analyzers)
        {
            var analyzerBytes = reader.GetAssemblyBytes(analyzer.Mvid);
            var assembly =  LoadFromStream(new MemoryStream(analyzerBytes.ToArray()));
            builder.Add(new(assembly));
        }

        AnalyzerReferences = builder.ToImmutable();
    }

    internal ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string languageName)
    {
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (var reference in AnalyzerReferences)
        {
            reference.GetAnalyzers(builder, languageName);
        }
        return builder.ToImmutable();
    }

    internal ImmutableArray<ISourceGenerator> GetGenerators(string languageName)
    {
        var builder = ImmutableArray.CreateBuilder<ISourceGenerator>();
        foreach (var reference in AnalyzerReferences)
        {
            reference.GetGenerators(builder, languageName);
        }
        return builder.ToImmutable();
    }
}
