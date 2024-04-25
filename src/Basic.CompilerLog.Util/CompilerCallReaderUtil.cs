namespace Basic.CompilerLog.Util;

public static class CompilerCallReaderUtil
{
    public static ICompilerCallReader Create(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (ext is ".binlog")
        {
            return BinaryLogReader.Create(filePath);
        }

        if (ext is ".complog")
        {
            return CompilerLogReader.Create(filePath);
        }

        throw new ArgumentException($"Unrecognized extension: {ext}");
    }
}
