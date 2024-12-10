using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Basic.CompilerLog.Util;

public interface ICompilerCallReader : IDisposable
{
    public BasicAnalyzerKind BasicAnalyzerKind { get; }
    public LogReaderState LogReaderState { get; }
    public bool OwnsLogReaderState { get; }
    public CompilerCall ReadCompilerCall(int index);
    public List<CompilerCall> ReadAllCompilerCalls(Func<CompilerCall, bool>? predicate = null);
    public List<CompilationData> ReadAllCompilationData(Func<CompilerCall, bool>? predicate = null);
    public CompilationData ReadCompilationData(CompilerCall compilerCall);
    public CompilerCallData ReadCompilerCallData(CompilerCall compilerCall);
    public SourceText ReadSourceText(SourceTextData sourceTextData);

    /// <summary>
    /// Read all of the <see cref="SourceTextData"/> for documents passed to the compilation
    /// </summary>
    public List<SourceTextData> ReadAllSourceTextData(CompilerCall compilerCall);

    /// <summary>
    /// Read all of the <see cref="AssemblyData"/> for references passed to the compilation
    /// </summary>
    public List<ReferenceData> ReadAllReferenceData(CompilerCall compilerCall);

    /// <summary>
    /// Read all of the <see cref="AssemblyData"/> for analyzers passed to the compilation
    /// </summary>
    public List<AnalyzerData> ReadAllAnalyzerData(CompilerCall compilerCall);

    /// <summary>
    /// Read all of the compilers used in this build.
    /// </summary>
    public List<CompilerAssemblyData> ReadAllCompilerAssemblies();

    public BasicAnalyzerHost CreateBasicAnalyzerHost(CompilerCall compilerCall);

    public bool TryGetCompilerCallIndex(Guid mvid, out int compilerCallIndex);

    /// <summary>
    /// Copy the bytes of the <paramref name="referenceData"/> to the provided <paramref name="stream"/>
    /// </summary>
    public void CopyAssemblyBytes(AssemblyData referenceData, Stream stream);

    public MetadataReference ReadMetadataReference(ReferenceData referenceData);

    /// <summary>
    /// Are all the generated files contained in the data? This should be true in the cases
    /// where there are provably no generated files (like a compilation without analyzers)
    /// </summary>
    /// <remarks>
    /// This can fail in a few cases
    ///   - Older compiler log versions don't encode all the data
    ///   - Compilations using native PDBS don't have this capability
    /// </remarks>
    public bool HasAllGeneratedFileContent(CompilerCall compilerCall);

    /// <summary>
    /// Read the set of generated sources from the compilation. This should only be called
    /// when <see cref="HasAllGeneratedFileContent(CompilerCall)"/> returns true
    /// </summary>
    public List<(SourceText SourceText, string FilePath)> ReadAllGeneratedSourceTexts(CompilerCall compilerCall);
}