using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace Basic.CompilerLog.Util;

public sealed class SolutionReader : IDisposable
{
    private List<ProjectId> _projectIdList;

    internal CompilerLogReader Reader { get; }
    internal VersionStamp VersionStamp { get; }
    internal SolutionId SolutionId { get; } = SolutionId.CreateNewId();

    public int ProjectCount => Reader.Count;

    internal SolutionReader(CompilerLogReader reader, VersionStamp? versionStamp = null)
    {
        Reader = reader;
        VersionStamp = versionStamp ?? VersionStamp.Default;

        _projectIdList = new List<ProjectId>(reader.Count);
        for (int i = 0; i < reader.Count; i++)
        {
            _projectIdList.Add(ProjectId.CreateNewId(debugName: i.ToString()));
        }
    }

    public void Dispose()
    {
        Reader.Dispose();
    }

    public static SolutionReader Create(Stream stream, bool leaveOpen = false) => new (CompilerLogReader.Create(stream, leaveOpen));

    public static SolutionReader Create(string filePath)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return new(CompilerLogReader.Create(stream, leaveOpen: false));
    }

    public SolutionInfo ReadSolutionInfo()
    {
        var guard = new object();
        var projectInfoList = new List<ProjectInfo>();
        for (var i = 0; i < ProjectCount; i++)
        {
            projectInfoList.Add(ReadProjectInfo(i));
        }

        return SolutionInfo.Create(SolutionId, VersionStamp, projects: projectInfoList);
    }

    public ProjectInfo ReadProjectInfo(int index)
    {
        var (compilerCall, rawCompilationData) = Reader.ReadRawCompilationData(index);
        var projectId = _projectIdList[index];
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
                case RawContentKind.AdditionalText:
                    Add(additionalDocuments);
                    break;
                case RawContentKind.AnalyzerConfig:
                    Add(analyzerConfigDocuments);
                    break;
                case RawContentKind.Embed:
                    // Not exposed yet
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

        // TODO: should actually store this information in the log so we can rehydrate
        var projectReferences = new List<ProjectReference>();

        var referenceList = Reader.GetMetadataReferences(rawCompilationData.References);
        var analyzers = Reader.ReadAnalyzers(rawCompilationData.Analyzers);
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp,
            name: compilerCall.ProjectFileName,
            assemblyName: rawCompilationData.Arguments.OutputFileName!,
            language: compilerCall.IsCSharp ? LanguageNames.CSharp : LanguageNames.VisualBasic,
            filePath: compilerCall.ProjectFilePath,
            outputFilePath: Path.Combine(rawCompilationData.Arguments.OutputDirectory, rawCompilationData.Arguments.OutputFileName!),
            compilationOptions: rawCompilationData.Arguments.CompilationOptions,
            parseOptions: rawCompilationData.Arguments.ParseOptions,
            documents,
            projectReferences,
            referenceList,
            analyzerReferences: analyzers.AnalyzerReferences,
            additionalDocuments,
            isSubmission: false,
            hostObjectType: null);

        return projectInfo.WithAnalyzerConfigDocuments(analyzerConfigDocuments);
    }
}
