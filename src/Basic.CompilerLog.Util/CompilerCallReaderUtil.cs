namespace Basic.CompilerLog.Util;

public static class CompilerCallReaderUtil
{
    /// <summary>
    /// Create an <see cref="ICompilerCallReader"/> directly over the provided file path
    /// </summary>
    public static ICompilerCallReader Create(string filePath, BasicAnalyzerKind? basicAnalyzerKind = null, LogReaderState? logReaderState = null)
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
}
