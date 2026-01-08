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
            // Check if the zip contains a single .complog entry
            // If yes: it's a zipped complog file (extract it)
            // If no: the zip file itself is a renamed .complog file
            using (var zipArchive = System.IO.Compression.ZipFile.OpenRead(filePath))
            {
                var complogEntries = zipArchive.Entries
                    .Where(x => Path.GetExtension(x.FullName) == ".complog")
                    .ToList();

                if (complogEntries.Count == 1)
                {
                    // This is a zipped complog file, extract it
                    if (CompilerLogUtil.TryCopySingleFileWithExtensionFromZip(filePath, ".complog") is { } c)
                    {
                        return CompilerLogReader.Create(c, basicAnalyzerKind, logReaderState, leaveOpen: false);
                    }
                }

                // Check for binlog as fallback
                var binlogEntries = zipArchive.Entries
                    .Where(x => Path.GetExtension(x.FullName) == ".binlog")
                    .ToList();

                if (binlogEntries.Count == 1)
                {
                    if (CompilerLogUtil.TryCopySingleFileWithExtensionFromZip(filePath, ".binlog") is { } b)
                    {
                        return BinaryLogReader.Create(b, basicAnalyzerKind, logReaderState, leaveOpen: false);
                    }
                }
            }

            // No nested .complog or .binlog found, treat the .zip as a renamed .complog
            return CompilerLogReader.Create(filePath, basicAnalyzerKind, logReaderState);
        }
    }

}
