using System.IO.Compression;

namespace Basic.CompilerLogger;

public static class CompilerLogUtil
{
    public static void WriteTo(Stream compilerLogStream, Stream binaryLogStream, List<string> diagnosticList)
    {
        using var zipStream = new ZipArchive(compilerLogStream, ZipArchiveMode.Create, leaveOpen: true);
    }
}