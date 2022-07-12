using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLogger;

public abstract class CompilationData
{
    public Compilation Compilation { get; }
    public CommandLineArguments CommandLineArguments { get; }

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
