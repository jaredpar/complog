using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Basic.CompilerLogger;

public abstract class CompilationData
{
    public Compilation Compilation { get; }
    public CommandLineArguments CommandLineArguments { get; }

    public EmitOptions EmitOptions => CommandLineArguments.EmitOptions;

    protected CompilationData(
        Compilation compilation,
        CommandLineArguments commandLineArguments)
    {
        Compilation = compilation;
        CommandLineArguments = commandLineArguments;
    }
}

public abstract class CompilationData<TCompilation, TCommandLineArguments> : CompilationData
    where TCompilation : Compilation
    where TCommandLineArguments : CommandLineArguments

{
    protected CompilationData(
        TCompilation compilation,
        TCommandLineArguments commandLineArguments)
        :base(compilation, commandLineArguments)
    {
        
    }
}

public sealed class CSharpCompilationData : CompilationData<CSharpCompilation, CSharpCommandLineArguments>
{
    public CSharpCompilationData(
        CSharpCompilation compilation,
        CSharpCommandLineArguments commandLineArguments)
        :base(compilation, commandLineArguments)
    {

    }
}

public sealed class VisualBasicCompilationData : CompilationData<VisualBasicCompilation, VisualBasicCommandLineArguments>
{
    public VisualBasicCompilationData(
        VisualBasicCompilation compilation,
        VisualBasicCommandLineArguments commandLineArguments)
        : base(compilation, commandLineArguments)
    {
    }
}
