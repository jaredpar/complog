
using Microsoft.CodeAnalysis.Text;

namespace Basic.CompilerLog.Util;

internal interface IBasicAnalyzerHostDataProvider
{
    public LogReaderState LogReaderState { get; }
    public void CopyAssemblyBytes(AssemblyData data, Stream stream);
    public byte[] GetAssemblyBytes(AssemblyData data);

    /// <inheritdoc cref="ICompilerCallReader.ReadAllAnalyzerData(CompilerCall)"/>
    public List<AnalyzerData> ReadAllAnalyzerData(CompilerCall compilerCall);

    /// <inheritdoc cref="ICompilerCallReader.HasAllGeneratedFileContent(CompilerCall)"/>
    public bool HasAllGeneratedFileContent(CompilerCall compilerCall);

    /// <inheritdoc cref="ICompilerCallReader.ReadAllGeneratedSourceTexts(CompilerCall)"/>
    public List<(SourceText SourceText, string FilePath)> ReadAllGeneratedSourceTexts(CompilerCall compilerCall);
}