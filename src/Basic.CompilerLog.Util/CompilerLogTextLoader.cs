using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogTextLoader : TextLoader
{
    private readonly Dictionary<Guid, (string ContentHash, string FilePath, SourceHashAlgorithm HashAlgorithm)> _idToContentMap = new();
    private readonly Dictionary<string, Guid> _contentToIdMap = new();

    internal CompilerLogReader Reader { get; }
    internal VersionStamp VersionStamp { get; }

    internal CompilerLogTextLoader(CompilerLogReader reader, VersionStamp versionStamp)
    {
        Reader = reader;
        VersionStamp = versionStamp;
    }

    public override Task<TextAndVersion> LoadTextAndVersionAsync(Workspace workspace, DocumentId documentId, CancellationToken cancellationToken)
    {
        if (!_idToContentMap.TryGetValue(documentId.Id, out var tuple))
        {
            throw new InvalidOperationException();
        }

        var sourceText = Reader.GetSourceText(tuple.ContentHash, tuple.HashAlgorithm);
        var textAndVersion = TextAndVersion.Create(sourceText, VersionStamp, tuple.FilePath);
        return Task.FromResult(textAndVersion);
    }
}
