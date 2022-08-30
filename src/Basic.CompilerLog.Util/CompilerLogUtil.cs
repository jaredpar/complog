namespace Basic.CompilerLog.Util;

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

        if (ext is ".compilerlog")
        {
            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        throw new Exception($"Unrecognized extension: {ext}");
    }

    public static List<string> ConvertBinaryLog(string binaryLogFilePath, string compilerLogFilePath, Func<CompilerCall, bool>? predicate = null)
    {
        using var compilerLogStream = new FileStream(compilerLogFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var binaryLogStream = new FileStream(binaryLogFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ConvertBinaryLog(binaryLogStream, compilerLogStream);
    }

    public static List<string> ConvertBinaryLog(Stream binaryLogStream, Stream compilerLogStream, Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        var diagnosticList = new List<string>();
        var list = BinaryLogUtil.ReadCompilationTasks(binaryLogStream, diagnosticList);
        using var builder = new CompilerLogBuilder(compilerLogStream, diagnosticList);
        foreach (var compilerInvocation in list)
        {
            if (predicate(compilerInvocation))
            {
                builder.Add(compilerInvocation);
            }
        }
        return diagnosticList;
    }

    public static List<CompilerCall> ReadCompilerCalls(string compilerLogFilePath, Func<CompilerCall, bool>? predicate = null)
    {
        using var compilerLogStream = new FileStream(compilerLogFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadCompilerCalls(compilerLogStream, predicate);
    }

    public static List<CompilerCall> ReadCompilerCalls(Stream compilerLogStream, Func<CompilerCall, bool>? predicate = null)
    {
        using var reader = CompilationReader.Create(compilerLogStream);
        return reader.ReadCompilerCalls(predicate);
    }

    public static List<CompilationData> ReadCompilationDatas(string compilerLogFilePath, Func<CompilerCall, bool>? predicate = null)
    {
        using var compilerLogStream = new FileStream(compilerLogFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadCompilationDatas(compilerLogStream, predicate);
    }

    public static List<CompilationData> ReadCompilationDatas(Stream compilerLogStream, Func<CompilerCall, bool>? predicate = null)
    {
        using var reader = CompilationReader.Create(compilerLogStream);
        return reader.ReadCompilationDatas(predicate);
    }
}