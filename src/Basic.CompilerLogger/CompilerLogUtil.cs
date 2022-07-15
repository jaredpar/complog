namespace Basic.CompilerLogger;

public static class CompilerLogUtil
{
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
        predicate ??= static _ => true;
        var list = new List<CompilerCall>();
        using var reader = new CompilerLogReader(compilerLogStream);
        for (int i = 0; i < reader.CompilationCount; i++)
        {
            var compilerCall = reader.ReadCompilerCall(i);
            if (predicate(compilerCall))
            {
                list.Add(compilerCall);
            }
        }

        return list;
    }
}