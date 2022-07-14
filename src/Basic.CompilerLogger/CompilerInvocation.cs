using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Task = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Basic.CompilerLogger
{
    public enum CompilationKind
    {
        Regular,
        Sattelite
    }

    public sealed class CompilerInvocation
    {
        public Task Task { get; }
        public CommandLineArguments CommandLineArguments { get; }
        public string[] RawArguments { get; }
        public string ProjectFile { get; }
        public CompilationKind CompilationKind { get; }
        public string? TargetFramework { get; }

        public bool IsCSharp => CommandLineArguments is CSharpCommandLineArguments;
        public bool IsVisualBasic => CommandLineArguments is VisualBasicCommandLineArguments;

        internal CompilerInvocation(string projectFile, Task task, CompilationKind kind, string? targetFramework, CommandLineArguments commandLineArguments, string[] rawArguments)
        {
            ProjectFile = projectFile;
            Task = task;
            CompilationKind = kind;
            TargetFramework = targetFramework;
            CommandLineArguments = commandLineArguments;
            RawArguments = rawArguments;
        }
    }
}
