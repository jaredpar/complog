using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Task = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Basic.CompilerLogger
{
    internal enum CompilationKind
    {
        Regular,
        Sattelite
    }

    internal sealed class CompilerInvocation
    {
        internal Task Task { get; }
        internal CommandLineArguments CommandLineArguments { get; }
        internal string[] RawArguments { get; }
        internal string ProjectFile { get; }
        internal CompilationKind CompilationKind { get; }

        internal bool IsCSharp => CommandLineArguments is CSharpCommandLineArguments;
        internal bool IsVisualBasic => CommandLineArguments is VisualBasicCommandLineArguments;

        internal CompilerInvocation(string projectFile, Task task, CompilationKind kind, CommandLineArguments commandLineArguments, string[] rawArguments)
        {
            ProjectFile = projectFile;
            Task = task;
            CompilationKind = kind;
            CommandLineArguments = commandLineArguments;
            RawArguments = rawArguments;
        }
    }
}
