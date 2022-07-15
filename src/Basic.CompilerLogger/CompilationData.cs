using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Basic.CompilerLogger;

public abstract class CompilationData
{
    private ImmutableArray<DiagnosticAnalyzer> _analyzers;
    private ImmutableArray<ISourceGenerator> _generators;
    private (Compilation, ImmutableArray<Diagnostic>)? _afterGenerators;

    public CompilerCall CompilerCall { get; } 
    public Compilation Compilation { get; }
    public CommandLineArguments CommandLineArguments { get; }
    public ImmutableArray<AdditionalText> AdditionalTexts { get; }
    internal CompilerLogAssemblyLoadContext CompilerLogAssemblyLoadContext { get; }

    public EmitOptions EmitOptions => CommandLineArguments.EmitOptions;
    public bool IsCSharp => Compilation is CSharpCompilation;
    public bool VisualBasic => !IsCSharp;

    private protected CompilationData(
        CompilerCall compilerCall,
        Compilation compilation,
        CommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        CompilerLogAssemblyLoadContext compilerLogAssemblyLoadContext)
    {
        CompilerCall = compilerCall;
        Compilation = compilation;
        CommandLineArguments = commandLineArguments;
        AdditionalTexts = additionalTexts;
        CompilerLogAssemblyLoadContext = compilerLogAssemblyLoadContext;
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
        var tuple = CompilerLogAssemblyLoadContext.LoadAnalyzers(languageName);
        _analyzers = tuple.Analyzers.ToImmutableArray();
        _generators = tuple.Generators.ToImmutableArray();
    }

    protected abstract GeneratorDriver CreateGeneratorDriver();
}

public abstract class CompilationData<TCompilation, TCommandLineArguments> : CompilationData
    where TCompilation : Compilation
    where TCommandLineArguments : CommandLineArguments
{
    public new TCompilation Compilation => (TCompilation)base.Compilation;
    public new TCommandLineArguments CommandLineArguments => (TCommandLineArguments)base.CommandLineArguments;

    private protected CompilationData(
        CompilerCall compilerCall,
        TCompilation compilation,
        TCommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        CompilerLogAssemblyLoadContext compilerLogAssemblyLoadContext)
        :base(compilerCall, compilation, commandLineArguments, additionalTexts, compilerLogAssemblyLoadContext)
    {
        
    }
}

public sealed class CSharpCompilationData : CompilationData<CSharpCompilation, CSharpCommandLineArguments>
{
    internal CSharpCompilationData(
        CompilerCall compilerCall,
        CSharpCompilation compilation,
        CSharpCommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        CompilerLogAssemblyLoadContext compilerLogAssemblyLoadContext)
        :base(compilerCall, compilation, commandLineArguments, additionalTexts, compilerLogAssemblyLoadContext)
    {

    }

    // TODO: need to implement the analyzer config provider
    protected override GeneratorDriver CreateGeneratorDriver() =>
        CSharpGeneratorDriver.Create(GetGenerators(), AdditionalTexts, CommandLineArguments.ParseOptions);
}

public sealed class VisualBasicCompilationData : CompilationData<VisualBasicCompilation, VisualBasicCommandLineArguments>
{
    internal VisualBasicCompilationData(
        CompilerCall compilerCall,
        VisualBasicCompilation compilation,
        VisualBasicCommandLineArguments commandLineArguments,
        ImmutableArray<AdditionalText> additionalTexts,
        CompilerLogAssemblyLoadContext compilerLogAssemblyLoadContext)
        : base(compilerCall, compilation, commandLineArguments, additionalTexts, compilerLogAssemblyLoadContext)
    {
    }

    // TODO: need to implement the analyzer config provider
    protected override GeneratorDriver CreateGeneratorDriver() =>
        VisualBasicGeneratorDriver.Create(GetGenerators(), AdditionalTexts, CommandLineArguments.ParseOptions);
}
