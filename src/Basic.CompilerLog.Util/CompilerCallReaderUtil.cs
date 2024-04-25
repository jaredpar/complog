namespace Basic.CompilerLog.Util;

public static class CompilerCallReaderUtil
{
    /// <summary>
    /// Create an <see cref="ICompilerCallReader"/> directly over the provided file path
    /// </summary>
    public static ICompilerCallReader Get(string filePath, BasicAnalyzerKind? basicAnalyzerKind = null, LogReaderState? logReaderState = null)
    {
        var ext = Path.GetExtension(filePath);
        if (ext is ".binlog")
        {
            return BinaryLogReader.Create(filePath, basicAnalyzerKind, logReaderState);
        }

        if (ext is ".complog")
        {
            return CompilerLogReader.Create(filePath, basicAnalyzerKind, logReaderState);
        }

        throw new ArgumentException($"Unrecognized extension: {ext}");
    }

    /// <summary>
    /// Get or create an <see cref="ICompilerCallReader"/> over the provided file path. The implementation
    /// may convert binary logs to compiler logs if the provided arguments aren't compatible with 
    /// a binary log. For example if <see cref="BasicAnalyzerKind.None"/> is provided
    /// </summary>
    public static ICompilerCallReader GetOrCreate(string filePath, BasicAnalyzerKind? basicAnalyzerKind = null, LogReaderState? logReaderState = null)
    {
        var ext = Path.GetExtension(filePath);
        if (ext is ".binlog")
        {
            if (basicAnalyzerKind is BasicAnalyzerKind.None)
            {
                var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
                return CompilerLogReader.Create(stream, basicAnalyzerKind, logReaderState, leaveOpen: false);
            }
        }

        return Get(filePath, basicAnalyzerKind, logReaderState);
    }
}
