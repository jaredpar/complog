using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Basic.CompilerLog.Util;

internal sealed class CompilerLogTextLoader : TextLoader
{
    private readonly Dictionary<DocumentId, (ProjectId ProjectId, string ContentHash, string FilePath, SourceHashAlgorithm HashAlgorithm)> _idToContentMap = new();
    private readonly Dictionary<(ProjectId ProjectId, string FilePath, string ContentHash, SourceHashAlgorithm HashAlgorithm), DocumentId> _contentToIdMap = new();

    internal CompilerLogReader Reader { get; }
    internal VersionStamp VersionStamp { get; }

    internal CompilerLogTextLoader(CompilerLogReader reader, VersionStamp versionStamp)
    {
        Reader = reader;
        VersionStamp = versionStamp;
    }

    internal DocumentId GetDocumentId(ProjectId projectId, string filePath, string contentHash, SourceHashAlgorithm hashAlgorithm)
    {
        var key = (projectId, filePath, contentHash, hashAlgorithm);
        if (!_contentToIdMap.TryGetValue(key, out var id))
        {
            id = DocumentId.CreateNewId(projectId, filePath);
            _contentToIdMap[key] = id;
        }

        return id;
    }

    public override Task<TextAndVersion> LoadTextAndVersionAsync(Workspace workspace, DocumentId documentId, CancellationToken cancellationToken)
    {
        if (!_idToContentMap.TryGetValue(documentId, out var tuple))
        {
            throw new InvalidOperationException();
        }

        var sourceText = Reader.GetSourceText(tuple.ContentHash, tuple.HashAlgorithm);
        var textAndVersion = TextAndVersion.Create(sourceText, VersionStamp, tuple.FilePath);
        return Task.FromResult(textAndVersion);
    }
}
