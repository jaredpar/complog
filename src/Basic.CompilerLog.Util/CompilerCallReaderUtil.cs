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
            // First try to open the .zip file directly as a complog
            // (handles case where .complog is renamed to .zip)
            try
            {
                return CompilerLogReader.Create(filePath, basicAnalyzerKind, logReaderState);
            }
            catch (CompilerLogException)
            {
                // Not a valid complog, try extracting nested files
            }

            // Fall back to existing logic - look for nested .complog or .binlog files
            if (CompilerLogUtil.TryCopySingleFileWithExtensionFromZip(filePath, ".complog") is { } c)
            {
                return CompilerLogReader.Create(c, basicAnalyzerKind, logReaderState, leaveOpen: false);
            }

            if (CompilerLogUtil.TryCopySingleFileWithExtensionFromZip(filePath, ".binlog") is { } b)
            {
                return BinaryLogReader.Create(b, basicAnalyzerKind, logReaderState, leaveOpen: false);
            }

            throw new Exception($"Could not find a .complog or .binlog file in {filePath}");
        }
    }

}
