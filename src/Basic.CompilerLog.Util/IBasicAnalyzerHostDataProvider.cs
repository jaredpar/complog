
namespace Basic.CompilerLog.Util;

internal interface IBasicAnalyzerHostDataProvider
{
    public LogReaderState LogReaderState { get; }
    public void CopyAssemblyBytes(RawAnalyzerData data, Stream stream);
}