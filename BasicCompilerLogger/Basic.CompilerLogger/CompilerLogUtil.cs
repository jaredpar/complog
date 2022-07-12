using System.IO.Compression;

namespace Basic.CompilerLogger;

public static class CompilerLogUtil
{
    public static void WriteTo(string compilerLogFilePath, string binaryLogFilePath, List<string> diagnosticList)
    {
        using var compilerLogStream = new FileStream(compilerLogFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var binaryLogStream = new FileStream(binaryLogFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        WriteTo(compilerLogStream, binaryLogStream, diagnosticList);
    }

    public static void WriteTo(Stream compilerLogStream, Stream binaryLogStream, List<string> diagnosticList)
    {
        var list = BinaryLogUtil.ReadCompilationTasks(binaryLogStream, diagnosticList);
        using var builder = new CompilerLogBuilder(compilerLogStream, diagnosticList);
        foreach (var compilerInvocation in list)
        {
            builder.Add(compilerInvocation);
        }
    }
}