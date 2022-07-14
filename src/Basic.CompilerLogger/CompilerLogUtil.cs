using System.IO.Compression;

namespace Basic.CompilerLogger;

public static class CompilerLogUtil
{
    public static List<string> ConvertBinaryLog(string binaryLogFilePath, string compilerLogFilePath, Func<CompilerInvocation, bool>? predicate = null)
    {
        using var compilerLogStream = new FileStream(compilerLogFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var binaryLogStream = new FileStream(binaryLogFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ConvertBinaryLog(binaryLogStream, compilerLogStream);
    }

    public static List<string> ConvertBinaryLog(Stream binaryLogStream, Stream compilerLogStream, Func<CompilerInvocation, bool>? predicate = null)
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

    public static List<CompilationData> ReadFrom(Stream compilerLogStream)
    {
        throw null!;
    }
}