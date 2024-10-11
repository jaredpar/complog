using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace Basic.CompilerLog.Util;

public sealed class SolutionReader : IDisposable
{
    private readonly SortedDictionary<int, (CompilerCall CompilerCall, ProjectId ProjectId)> _indexToProjectDataMap;

    internal CompilerLogReader Reader { get; }
    internal VersionStamp VersionStamp { get; }
    internal SolutionId SolutionId { get; } = SolutionId.CreateNewId();

    public int ProjectCount => _indexToProjectDataMap.Count;

    internal SolutionReader(CompilerLogReader reader, Func<CompilerCall, bool>? predicate = null, VersionStamp? versionStamp = null)
    {
        Reader = reader;
        VersionStamp = versionStamp ?? VersionStamp.Default;

        predicate ??= static c => c.Kind == CompilerCallKind.Regular;
        var map = new SortedDictionary<int, (CompilerCall, ProjectId)>();
        for (int i = 0; i < reader.Count; i++)
        {
            var call = reader.ReadCompilerCall(i);
            if (predicate(call))
            {
                var projectId = ProjectId.CreateNewId(debugName: i.ToString());
                map[i] = (call, projectId);
            }
        }

        _indexToProjectDataMap = map;
    }

    public void Dispose()
    {
        Reader.Dispose();
    }

    public static SolutionReader Create(Stream stream, BasicAnalyzerKind? basicAnalyzerKind = null, LogReaderState? state = null, bool leaveOpen = false, Func<CompilerCall, bool>? predicate = null) => 
        new (CompilerLogReader.Create(stream, basicAnalyzerKind, state, leaveOpen), predicate);

    public static SolutionReader Create(string filePath, BasicAnalyzerKind? basicAnalyzerKind = null, LogReaderState? state = null, Func<CompilerCall, bool>? predicate = null)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return new(CompilerLogReader.Create(stream, basicAnalyzerKind, state, leaveOpen: false), predicate);
    }

    public SolutionInfo ReadSolutionInfo()
    {
        var projectInfoList = new List<ProjectInfo>(capacity: ProjectCount);
        foreach (var kvp in _indexToProjectDataMap)
        {
            projectInfoList.Add(ReadProjectInfo(kvp.Value.CompilerCall, kvp.Value.ProjectId));
        }

        return SolutionInfo.Create(SolutionId, VersionStamp, projects: projectInfoList);
    }

    private ProjectInfo ReadProjectInfo(CompilerCall compilerCall, ProjectId projectId)
    {
        var rawCompilationData = Reader.ReadRawCompilationData(compilerCall);
        var documents = new List<DocumentInfo>();
        var additionalDocuments = new List<DocumentInfo>();
        var analyzerConfigDocuments = new List<DocumentInfo>();

        foreach (var tuple in rawCompilationData.Contents)
        {
            switch (tuple.Kind)
            {
                case RawContentKind.SourceText:
                    Add(documents);
                    break;
                case RawContentKind.GeneratedText:
                    // These are handled by theh generators
                    break;
                case RawContentKind.AdditionalText:
                    Add(additionalDocuments);
                    break;
                case RawContentKind.AnalyzerConfig:
                    Add(analyzerConfigDocuments);
                    break;
                case RawContentKind.SourceLink:
                case RawContentKind.RuleSet:
                case RawContentKind.AppConfig:
                case RawContentKind.Win32Manifest:
                case RawContentKind.Win32Resource:
                case RawContentKind.Win32Icon:
                case RawContentKind.CryptoKeyFile:
                case RawContentKind.Embed:
                case RawContentKind.EmbedLine:
                    // Not exposed via the workspace APIs yet
                    break;
                default:
                    throw new InvalidOperationException();
            }

            void Add(List<DocumentInfo> list)
            {
                var documentId = DocumentId.CreateNewId(projectId, debugName: Path.GetFileName(tuple.FilePath));
                list.Add(DocumentInfo.Create(
                    documentId,
                    Path.GetFileName(tuple.FilePath),
                    loader: new CompilerLogTextLoader(Reader, VersionStamp, tuple.ContentHash, tuple.FilePath), 
                    filePath: tuple.FilePath));
            }
        }

        var refTuple = ReadReferences(rawCompilationData.References);
        var analyzers = Reader.ReadAnalyzers(rawCompilationData);
        var options = Reader.ReadCompilerOptions(compilerCall);
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp,
            name: compilerCall.ProjectFileName,
            assemblyName: rawCompilationData.AssemblyFileName,
            language: compilerCall.IsCSharp ? LanguageNames.CSharp : LanguageNames.VisualBasic,
            filePath: compilerCall.ProjectFilePath,
            outputFilePath: Path.Combine(rawCompilationData.OutputDirectory ?? "", rawCompilationData.AssemblyFileName),
            compilationOptions: options.CompilationOptions,
            parseOptions: options.ParseOptions,
            documents,
            refTuple.Item1,
            refTuple.Item2,
            analyzerReferences: analyzers.AnalyzerReferences,
            additionalDocuments,
            isSubmission: false,
            hostObjectType: null);

        return projectInfo.WithAnalyzerConfigDocuments(analyzerConfigDocuments);

        (List<ProjectReference>, List<MetadataReference>) ReadReferences(List<RawReferenceData> rawReferenceDataList)
        {
            // The command line compiler supports having the same reference added multiple times. It's actually
            // not uncommon for Microsoft.VisualBasic.dll to be passed twice when working on Visual Basic projects. 
            // The workspaces layer though cannot handle duplicates hence we need to run a de-dupe pass here.
            var hashSet = new HashSet<Guid>();
            var projectReferences = new List<ProjectReference>();
            var metadataReferences = new List<MetadataReference>(rawReferenceDataList.Count);
            foreach (var rawReferenceData in rawCompilationData.References)
            {
                if (!hashSet.Add(rawReferenceData.Mvid))
                {
                    continue;
                }

                if (Reader.TryGetCompilerCallIndex(rawReferenceData.Mvid, out var refCompilerCallIndex))
                {
                    var refProjectId = _indexToProjectDataMap[refCompilerCallIndex].ProjectId;
                    projectReferences.Add(new ProjectReference(refProjectId, rawReferenceData.Aliases, rawReferenceData.EmbedInteropTypes));
                }
                else
                {
                    var metadataReference = Reader.ReadMetadataReference(rawReferenceData);
                    metadataReferences.Add(metadataReference);
                }
            }

            return (projectReferences, metadataReferences);
        }
    }
}
