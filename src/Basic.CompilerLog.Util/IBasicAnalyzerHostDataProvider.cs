
namespace Basic.CompilerLog.Util;

internal interface IBasicAnalyzerHostDataProvider
{
    public CompilerLogState CompilerLogState { get; }
    public void CopyAssemblyBytes(RawAnalyzerData data, Stream stream);
}