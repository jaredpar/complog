using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Basic.CompilerLogger;

public abstract class CompilationData
{
    private ImmutableArray<DiagnosticAnalyzer> _analyzers;
    private ImmutableArray<ISourceGenerator> _generators;

    public Compilation Compilation { get; }
    public CommandLineArguments CommandLineArguments { get; }
    internal CompilerLogAssemblyLoadContext CompilerLogAssemblyLoadContext { get; }

    public EmitOptions EmitOptions => CommandLineArguments.EmitOptions;
    public bool IsCSharp => Compilation is CSharpCompilation;
    public bool VisualBasic => !IsCSharp;

    private protected CompilationData(
        Compilation compilation,
        CommandLineArguments commandLineArguments,
        CompilerLogAssemblyLoadContext compilerLogAssemblyLoadContext)
    {
        Compilation = compilation;
        CommandLineArguments = commandLineArguments;
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
}

public abstract class CompilationData<TCompilation, TCommandLineArguments> : CompilationData
    where TCompilation : Compilation
    where TCommandLineArguments : CommandLineArguments

{
    private protected CompilationData(
        TCompilation compilation,
        TCommandLineArguments commandLineArguments,
        CompilerLogAssemblyLoadContext compilerLogAssemblyLoadContext)
        :base(compilation, commandLineArguments, compilerLogAssemblyLoadContext)
    {
        
    }
}

public sealed class CSharpCompilationData : CompilationData<CSharpCompilation, CSharpCommandLineArguments>
{
    internal CSharpCompilationData(
        CSharpCompilation compilation,
        CSharpCommandLineArguments commandLineArguments,
        CompilerLogAssemblyLoadContext compilerLogAssemblyLoadContext)
        :base(compilation, commandLineArguments, compilerLogAssemblyLoadContext)
    {

    }
}

public sealed class VisualBasicCompilationData : CompilationData<VisualBasicCompilation, VisualBasicCommandLineArguments>
{
    internal VisualBasicCompilationData(
        VisualBasicCompilation compilation,
        VisualBasicCommandLineArguments commandLineArguments,
        CompilerLogAssemblyLoadContext compilerLogAssemblyLoadContext)
        : base(compilation, commandLineArguments, compilerLogAssemblyLoadContext)
    {
    }
}
