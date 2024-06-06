using System.Reflection;

namespace Basic.CompilerLog.Util;

public interface ICompilerCallReader : IDisposable
{
    public BasicAnalyzerKind BasicAnalyzerKind { get; }
    public LogReaderState LogReaderState { get; }
    public bool OwnsLogReaderState { get; }
    public List<CompilerCall> ReadAllCompilerCalls(Func<CompilerCall, bool>? predicate = null);
    public List<CompilationData> ReadAllCompilationData(Func<CompilerCall, bool>? predicate = null);
    public CompilationData ReadCompilationData(CompilerCall compilerCall);

    /// <summary>
    /// Read all of the <see cref="ReferenceData"/> for references passed to the compilation
    /// </summary>
    public List<ReferenceData> ReadAllReferenceData(CompilerCall compilerCall);

    /// <summary>
    /// Read all of the <see cref="ReferenceData"/> for analyzers passed to the compilation
    /// </summary>
    public List<ReferenceData> ReadAllAnalyzerData(CompilerCall compilerCall);


    // TODO: Use a type here instead of a tuple
    public List<(string CompilerFilePath, AssemblyName AssemblyName, string? CommitHash)> ReadAllCompilerAssemblies();
}