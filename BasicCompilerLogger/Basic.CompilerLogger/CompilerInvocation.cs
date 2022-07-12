using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Task = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Basic.CompilerLogger
{
    internal enum CompilerKind
    {
        CSharp,
        VisualBasic
    }

    internal sealed class CompilerInvocation
    {
        internal Task Task { get; }
        internal CommandLineArguments CommandLineArguments { get; }
        internal string[] RawArguments { get; }
        internal string ProjectFile { get; }

        internal CompilerKind CompilerKind =>
            CommandLineArguments is CSharpCommandLineArguments
            ? CompilerKind.CSharp
            : CompilerKind.VisualBasic;

        internal CompilerInvocation(string projectFile, Task task, CommandLineArguments commandLineArguments, string[] rawArguments)
        {
            ProjectFile = projectFile;
            Task = task;
            CommandLineArguments = commandLineArguments;
            RawArguments = rawArguments;
        }
    }
}
