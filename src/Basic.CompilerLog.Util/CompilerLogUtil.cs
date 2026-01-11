using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace Basic.CompilerLog.Util;

public readonly struct ConvertBinaryLogResult
{
    public bool Succeeded { get; }

    /// <summary>
    /// The set of <see cref="CompilerCall"/> included in the log
    /// </summary>
    public List<CompilerCall> CompilerCalls { get; }

    /// <summary>
    /// The diagnostics produced during conversion
    /// </summary>
    public List<string> Diagnostics { get; }

    public ConvertBinaryLogResult(bool succeeded, List<CompilerCall> compilerCalls, List<string> diagnostics)
    {
        Succeeded = succeeded;
        CompilerCalls = compilerCalls;
        Diagnostics = diagnostics;
    }
}

public static class CompilerLogUtil
{
    /// <summary>
    /// Opens or creates a valid compiler log stream from the provided file path. The file path
    /// must refer to a binary or compiler log
    /// </summary>
    public static Stream GetOrCreateCompilerLogStream(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext switch
        {
            ".binlog" => ConvertBinaryLogFile(filePath),
            ".complog" => new FileStream(filePath, FileMode.Open, FileAccess.Read),
            ".zip" => GetFromZip(filePath),
            _ => throw new ArgumentException($"Unrecognized extension: {ext}")
        };

        MemoryStream ConvertBinaryLogFile(string filePath)
        {
            using var fileStream = RoslynUtil.OpenBuildFileForRead(filePath);
            return ConvertBinaryLogStream(fileStream);
        }

        static MemoryStream ConvertBinaryLogStream(Stream binlogStream)
        {
            var memoryStream = new MemoryStream();
            _ = ConvertBinaryLog(binlogStream, memoryStream);
            memoryStream.Position = 0;
            return memoryStream;
        }

        static Stream GetFromZip(string zipFilePath)
        {
            var logStream = ReadLogFromZip(zipFilePath, out var isComplog);
            return isComplog ? logStream : ConvertBinaryLogStream(logStream);
        }
    }

    public static List<string> ConvertBinaryLog(string binaryLogFilePath, string compilerLogFilePath, Func<CompilerCall, bool>? predicate = null)
    {
        using var compilerLogStream = new FileStream(compilerLogFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var binaryLogStream = new FileStream(binaryLogFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ConvertBinaryLog(binaryLogStream, compilerLogStream, predicate);
    }

    public static List<string> ConvertBinaryLog(Stream binaryLogStream, Stream compilerLogStream, Func<CompilerCall, bool>? predicate = null)
    {
        var diagnostics = new List<string>();
        if (!TryConvertBinaryLog(binaryLogStream, compilerLogStream, diagnostics, predicate))
        {
            throw new CompilerLogException("Could not convert binary log", diagnostics);
        }

        return diagnostics;
    }

    public static bool TryConvertBinaryLog(Stream binaryLogStream, Stream compilerLogStream, List<string> diagnostics, Func<CompilerCall, bool>? predicate = null)
    {
        var result = TryConvertBinaryLog(binaryLogStream, compilerLogStream, predicate);
        diagnostics.AddRange(result.Diagnostics);
        return result.Succeeded;
    }

    public static ConvertBinaryLogResult TryConvertBinaryLog(string binaryLogFilePath, string compilerLogFilePath, Func<CompilerCall, bool>? predicate = null)
    {
        using var compilerLogStream = new FileStream(compilerLogFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var binaryLogStream = new FileStream(binaryLogFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return TryConvertBinaryLog(binaryLogStream, compilerLogStream, predicate);
    }

    public static ConvertBinaryLogResult TryConvertBinaryLog(Stream binaryLogStream, Stream compilerLogStream, Func<CompilerCall, bool>? predicate = null) =>
        TryConvertBinaryLog(binaryLogStream, compilerLogStream, predicate, metadataVersion: null);

    internal static ConvertBinaryLogResult TryConvertBinaryLog(Stream binaryLogStream, Stream compilerLogStream, Func<CompilerCall, bool>? predicate = null, int? metadataVersion = null)
    {
        predicate ??= static _ => true;
        var diagnostics = new List<string>();
        var included = new List<CompilerCall>();

        var success = true;
        var list = new List<BinaryLogUtil.CompilerTaskData>();
        try
        {
            list = BinaryLogUtil.ReadAllCompilerTaskData(binaryLogStream, predicate);
        }
        catch (EndOfStreamException ex)
        {
            diagnostics.Add($"Error reading binary log: {ex.GetType().FullName}: {ex.Message}");
            success = false;
        }

        using var builder = new CompilerLogBuilder(compilerLogStream, diagnostics, metadataVersion);
        foreach (var compilerTaskData in list)
        {
            try
            {
                builder.AddFromDisk(compilerTaskData.CompilerCall, compilerTaskData.Arguments);
                included.Add(compilerTaskData.CompilerCall);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Error adding {compilerTaskData.CompilerCall.ProjectFilePath}: {ex.Message}");
                success = false;
            }
        }

        return new ConvertBinaryLogResult(success, included, diagnostics);
    }

    /// <summary>
    /// Try and read a log file from the provided zip file path. This will consider all of the following
    /// cases:
    /// - zip file is just a renamed .complog file
    /// - zip has a single embedded .complog file
    /// - zip has a single embedded .binlog file
    /// Any other case will throw an exception
    /// </summary>
    internal static Stream ReadLogFromZip(string zipFilePath, out bool isComplog)
    {
        using var zipArchive = ZipFile.OpenRead(zipFilePath);
        if (zipArchive.GetEntry(CommonUtil.MetadataFileName) is not null)
        {
            zipArchive.Dispose();
            isComplog  = true;
            return RoslynUtil.OpenBuildFileForRead(zipFilePath);
        }

        if (zipArchive.Entries.SingleOrDefault(x => Path.GetExtension(x.Name) == ".complog") is { } complogEntry)
        {
            isComplog = true;
            return CopyArchiveEntry(complogEntry);
        }

        if (zipArchive.Entries.SingleOrDefault(x => Path.GetExtension(x.Name) == ".binlog") is { } binlogEntry)
        {
            isComplog = false;
            return CopyArchiveEntry(binlogEntry);
        }

        throw new Exception($"The zip file '{zipFilePath}' does not contain a single .complog or .binlog file");

        static MemoryStream CopyArchiveEntry(ZipArchiveEntry entry)
        {
            var memoryStream = new MemoryStream();
            using var entryStream = entry.Open();
            entryStream.CopyTo(memoryStream);
            memoryStream.Position = 0;
            return memoryStream;
        }
    }
}
