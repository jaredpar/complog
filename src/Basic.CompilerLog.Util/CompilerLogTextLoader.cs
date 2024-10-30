using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogTextLoader : TextLoader
{
    internal ICompilerCallReader Reader { get; }
    internal VersionStamp VersionStamp { get; }
    internal SourceTextData SourceTextData { get; }

    internal CompilerLogTextLoader(ICompilerCallReader reader, VersionStamp versionStamp, SourceTextData sourceTextData)
    {
        Reader = reader;
        VersionStamp = versionStamp;
        SourceTextData = sourceTextData;
    }

    public override Task<TextAndVersion> LoadTextAndVersionAsync(LoadTextOptions options, CancellationToken cancellationToken)
    {
        SourceText sourceText;

        // The loader can operate on multiple threads due to the nature of solutions and 
        // workspaces. Need to guard access here as the underlying data structures in the
        // reader are not safe for paralell reads.
        lock (Reader)
        {
            sourceText = Reader.ReadSourceText(SourceTextData);
        }

        var textAndVersion = TextAndVersion.Create(sourceText, VersionStamp, SourceTextData.FilePath);
        return Task.FromResult(textAndVersion);
    }
}
