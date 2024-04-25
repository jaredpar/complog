namespace Basic.CompilerLog.Util;

public interface ICompilerCallReader : IDisposable
{
    public BasicAnalyzerKind BasicAnalyzerKind { get; }
    public LogReaderState LogReaderState { get; }
    public bool OwnsLogReaderState { get; }
    public List<CompilerCall> ReadAllCompilerCalls(Func<CompilerCall, bool>? predicate = null);
    public List<CompilationData> ReadAllCompilationData(Func<CompilerCall, bool>? predicate = null);
}