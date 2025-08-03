
using Basic.CompilerLog.App;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

new CompilerLogApp().Run(args);

// This exists purely so that the test hooks can get a reference to this assembly 
// and validate that references are setup correctly
internal sealed class ProgramHolder 
{
    internal Compilation? Compilation { get; set; }
    internal CSharpCompilation? CSharpCompilation { get; set; }
    internal VisualBasicCompilation? VisualBasicCompilation { get; set; }
}