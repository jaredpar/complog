
namespace Basic.CompilerLog.Util;

internal interface IBasicAnalyzerHostDataProvider
{
    public LogReaderState LogReaderState { get; }
    public void CopyAssemblyBytes(AssemblyData data, Stream stream);
    public byte[] GetAssemblyBytes(AssemblyData data);
}