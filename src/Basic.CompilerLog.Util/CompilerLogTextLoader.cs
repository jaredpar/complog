using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Web;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogTextLoader : TextLoader
{
    internal CompilerLogReader Reader { get; }
    internal VersionStamp VersionStamp { get; }
    internal string ContentHash { get; }
    internal string FilePath { get; }

    internal CompilerLogTextLoader(CompilerLogReader reader, VersionStamp versionStamp, string contentHash, string filePath)
    {
        Reader = reader;
        VersionStamp = versionStamp;
        ContentHash = contentHash;
        FilePath = filePath;
    }

    public override Task<TextAndVersion> LoadTextAndVersionAsync(LoadTextOptions options, CancellationToken cancellationToken)
    {
        SourceText sourceText;

        // The loader can operate on multiple threads due to the nature of solutions and 
        // workspaces. Need to guard access here as the underlying data structures in the
        // reader are not safe for paralell reads.
        lock (Reader)
        {
            sourceText = Reader.GetSourceText(ContentHash, options.ChecksumAlgorithm);
        }

        var textAndVersion = TextAndVersion.Create(sourceText, VersionStamp, FilePath);
        return Task.FromResult(textAndVersion);
    }
}
