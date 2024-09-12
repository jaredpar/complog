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
        if (ext is ".binlog")
        {
            var memoryStream = new MemoryStream();
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ConvertBinaryLog(fileStream, memoryStream);
            memoryStream.Position = 0;
            return memoryStream;
        }

        if (ext is ".complog")
        {
            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        throw new Exception($"Unrecognized extension: {ext}");
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

        var list = BinaryLogUtil.ReadAllCompilerCalls(binaryLogStream, predicate);
        using var builder = new CompilerLogBuilder(compilerLogStream, diagnostics, metadataVersion);
        var success = true;
        foreach (var compilerCall in list)
        {
            try
            {
                var commandLineArguments = BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall);
                builder.AddFromDisk(compilerCall, commandLineArguments);
                included.Add(compilerCall);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Error adding {compilerCall.ProjectFilePath}: {ex.Message}");
                success = false;
            }
        }

        return new ConvertBinaryLogResult(success, included, diagnostics);
    }
}