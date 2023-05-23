using Basic.CompilerLog.Util.Impl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Basic.CompilerLog.Util;

public abstract class CompilationData
{
    private ImmutableArray<DiagnosticAnalyzer> _analyzers;
    private ImmutableArray<ISourceGenerator> _generators;
    private (Compilation, ImmutableArray<Diagnostic>)? _afterGenerators;
    private readonly CommandLineArguments _commandLineArguments;

    public CompilerCall CompilerCall { get; } 
    public Compilation Compilation { get; }
    public BasicAnalyzerHost BasicAnalyzerHost { get; }
    public ImmutableArray<AdditionalText> AdditionalTexts { get; }
    public ImmutableArray<AnalyzerReference> AnalyzerReferences { get; }
    public AnalyzerConfigOptionsProvider AnalyzerConfigOptionsProvider { get; }

    public CompilationOptions CompilationOptions => Compilation.Options;
    public EmitOptions EmitOptions => _commandLineArguments.EmitOptions;
    public ParseOptions ParseOptions => _commandLineArguments.ParseOptions;
    public bool IsCSharp => Compilation is CSharpCompilation;
    public bool VisualBasic => !IsCSharp;

    private protected CompilationData(
        CompilerCall compilerCall,
        Compilation compilation,
        CommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider)
    {
        CompilerCall = compilerCall;
        Compilation = compilation;
        _commandLineArguments = commandLineArguments;
        AdditionalTexts = additionalTexts;
        BasicAnalyzerHost = basicAnalyzerHost;
        AnalyzerReferences = basicAnalyzerHost.AnalyzerReferences;
        AnalyzerConfigOptionsProvider = analyzerConfigOptionsProvider;
    }

    public ImmutableArray<DiagnosticAnalyzer> GetAnalyzers()
    {
        EnsureAnalyzersLoaded();
        return _analyzers;
    }

    public ImmutableArray<ISourceGenerator> GetGenerators()
    {
        EnsureAnalyzersLoaded();
        return _generators;
    }

    public Compilation GetCompilationAfterGenerators() =>
        GetCompilationAfterGenerators(out _);

    public Compilation GetCompilationAfterGenerators(out ImmutableArray<Diagnostic> diagnostics)
    {
        if (_afterGenerators is { } tuple)
        {
            diagnostics = tuple.Item2;
            return tuple.Item1;
        }

        var driver = CreateGeneratorDriver();
        driver.RunGeneratorsAndUpdateCompilation(Compilation, out tuple.Item1, out tuple.Item2);
        _afterGenerators = tuple;
        diagnostics = tuple.Item2;
        return tuple.Item1;
    }

    private void EnsureAnalyzersLoaded()
    {
        if (!_analyzers.IsDefault)
        {
            Debug.Assert(!_generators.IsDefault);
            return;
        }

        var languageName = IsCSharp ? LanguageNames.CSharp : LanguageNames.VisualBasic;
        var analyzerBuilder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var generatorBuilder = ImmutableArray.CreateBuilder<ISourceGenerator>();
        foreach (var analyzerReference in AnalyzerReferences)
        {
            analyzerBuilder.AddRange(analyzerReference.GetAnalyzers(languageName));
            generatorBuilder.AddRange(analyzerReference.GetGenerators(languageName));
        }

        _analyzers = analyzerBuilder.ToImmutableArray();
        _generators = generatorBuilder.ToImmutableArray();
    }

    protected abstract GeneratorDriver CreateGeneratorDriver();
}

public abstract class CompilationData<TCompilation, TParseOptions> : CompilationData
    where TCompilation : Compilation
    where TParseOptions : ParseOptions
{
    public new TCompilation Compilation => (TCompilation)base.Compilation;
    public new TParseOptions ParseOptions => (TParseOptions)base.ParseOptions;

    private protected CompilationData(
        CompilerCall compilerCall,
        TCompilation compilation,
        CommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider)
        :base(compilerCall, compilation, commandLineArguments, additionalTexts, basicAnalyzerHost, analyzerConfigOptionsProvider)
    {
        
    }
}

public sealed class CSharpCompilationData : CompilationData<CSharpCompilation, CSharpParseOptions>
{
    internal CSharpCompilationData(
        CompilerCall compilerCall,
        CSharpCompilation compilation,
        CSharpCommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider)
        :base(compilerCall, compilation, commandLineArguments, additionalTexts, basicAnalyzerHost, analyzerConfigOptionsProvider)
    {

    }

    protected override GeneratorDriver CreateGeneratorDriver() =>
        CSharpGeneratorDriver.Create(GetGenerators(), AdditionalTexts, ParseOptions, AnalyzerConfigOptionsProvider);
}

public sealed class VisualBasicCompilationData : CompilationData<VisualBasicCompilation, VisualBasicParseOptions>
{
    internal VisualBasicCompilationData(
        CompilerCall compilerCall,
        VisualBasicCompilation compilation,
        VisualBasicCommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        BasicAnalyzerHost basicAnalyzerHost,
        AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider)
        : base(compilerCall, compilation, commandLineArguments, additionalTexts, basicAnalyzerHost, analyzerConfigOptionsProvider)
    {
    }

    protected override GeneratorDriver CreateGeneratorDriver() =>
        VisualBasicGeneratorDriver.Create(GetGenerators(), AdditionalTexts, ParseOptions, AnalyzerConfigOptionsProvider);
}
