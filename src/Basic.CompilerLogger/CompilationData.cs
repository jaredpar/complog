using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Basic.CompilerLogger;

public abstract class CompilationData
{
    public Compilation Compilation { get; }
    public CommandLineArguments CommandLineArguments { get; }
    internal CompilerLogAssemblyLoadContext CompilerLogAssemblyLoadContext { get; }

    public EmitOptions EmitOptions => CommandLineArguments.EmitOptions;

    private protected CompilationData(
        Compilation compilation,
        CommandLineArguments commandLineArguments,
        CompilerLogAssemblyLoadContext compilerLogAssemblyLoadContext)
    {
        Compilation = compilation;
        CommandLineArguments = commandLineArguments;
        CompilerLogAssemblyLoadContext = compilerLogAssemblyLoadContext;
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
