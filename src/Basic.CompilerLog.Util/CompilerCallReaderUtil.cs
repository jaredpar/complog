using System.IO.Compression;
using Microsoft.Build.Logging.StructuredLogger;

namespace Basic.CompilerLog.Util;

public static class CompilerCallReaderUtil
{
    /// <summary>
    /// Create an <see cref="ICompilerCallReader"/> directly over the provided file path
    /// </summary>
    public static ICompilerCallReader Create(string filePath, BasicAnalyzerKind? basicAnalyzerKind = null, LogReaderState? logReaderState = null)
    {
        var ext = Path.GetExtension(filePath);
        return ext switch
        {
            ".binlog" => BinaryLogReader.Create(filePath, basicAnalyzerKind, logReaderState),
            ".complog" => CompilerLogReader.Create(filePath, basicAnalyzerKind, logReaderState),
            ".zip" => CreateFromZip(),
            _ => throw new ArgumentException($"Unrecognized extension: {ext}")
        };

        ICompilerCallReader CreateFromZip()
        {
            var stream = CompilerLogUtil.ReadLogFromZip(filePath, out var isComplog);
            return isComplog
                ? CompilerLogReader.Create(stream, basicAnalyzerKind, logReaderState, leaveOpen: false)
                : BinaryLogReader.Create(stream, basicAnalyzerKind, logReaderState, leaveOpen: false);
        }
    }

}
