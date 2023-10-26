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
    private readonly ImmutableArray<(CompilerCall CompilerCall, ProjectId ProjectId)> _projectDataList;

    internal CompilerLogReader Reader { get; }
    internal VersionStamp VersionStamp { get; }
    internal SolutionId SolutionId { get; } = SolutionId.CreateNewId();

    public int ProjectCount => _projectDataList.Length;

    internal SolutionReader(CompilerLogReader reader, Func<CompilerCall, bool>? predicate = null, VersionStamp? versionStamp = null)
    {
        Reader = reader;
        VersionStamp = versionStamp ?? VersionStamp.Default;

        predicate ??= static c => c.Kind == CompilerCallKind.Regular;
        var builder = ImmutableArray.CreateBuilder<(CompilerCall, ProjectId)>();
        for (int i = 0; i < reader.Count; i++)
        {
            var call = reader.ReadCompilerCall(i);
            if (predicate(call))
            {
                var projectId = ProjectId.CreateNewId(debugName: i.ToString());
                builder.Add((call, projectId));
            }
        }
        _projectDataList = builder.ToImmutableArray();
    }

    public void Dispose()
    {
        Reader.Dispose();
    }

    public static SolutionReader Create(Stream stream, bool leaveOpen = false, BasicAnalyzerHostOptions? options = null, Func<CompilerCall, bool>? predicate = null) => 
        new (CompilerLogReader.Create(stream, leaveOpen, options), predicate);

    public static SolutionReader Create(string filePath, BasicAnalyzerHostOptions? options = null, Func<CompilerCall, bool>? predicate = null)
    {
        var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(filePath);
        return new(CompilerLogReader.Create(stream, leaveOpen: false, options), predicate);
    }

    public SolutionInfo ReadSolutionInfo()
    {
        var projectInfoList = new List<ProjectInfo>();
        for (var i = 0; i < ProjectCount; i++)
        {
            projectInfoList.Add(ReadProjectInfo(i));
        }

        return SolutionInfo.Create(SolutionId, VersionStamp, projects: projectInfoList);
    }

    public ProjectInfo ReadProjectInfo(int index)
    {
        var (compilerCall, projectId) = _projectDataList[index];
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
                    // Handled when creating analyzer host.
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

        // TODO: should actually store this information in the log so we can rehydrate
        var projectReferences = new List<ProjectReference>();

        var referenceList = Reader.GetMetadataReferences(FilterToUnique(rawCompilationData.References));
        var analyzers = Reader.ReadAnalyzers(rawCompilationData);
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp,
            name: compilerCall.ProjectFileName,
            assemblyName: rawCompilationData.AssemblyFileName,
            language: compilerCall.IsCSharp ? LanguageNames.CSharp : LanguageNames.VisualBasic,
            filePath: compilerCall.ProjectFilePath,
            outputFilePath: Path.Combine(rawCompilationData.OutputDirectory ?? "", rawCompilationData.AssemblyFileName),
            compilationOptions: Reader.ReadCompilationOptions(rawCompilationData),
            parseOptions: Reader.ReadParseOptions(rawCompilationData),
            documents,
            projectReferences,
            referenceList,
            analyzerReferences: analyzers.AnalyzerReferences,
            additionalDocuments,
            isSubmission: false,
            hostObjectType: null);

        return projectInfo.WithAnalyzerConfigDocuments(analyzerConfigDocuments);

        // The command line compiler supports having the same reference added multiple times. It's actually
        // not uncommon for Microsoft.VisualBasic.dll to be passed twice when working on Visual Basic projects. 
        // The workspaces layer though cannot handle duplicates hence we need to run a de-dupe pass here.
        static List<RawReferenceData> FilterToUnique(List<RawReferenceData> referenceList)
        {
            var hashSet = new HashSet<Guid>();
            var list = new List<RawReferenceData>(capacity: referenceList.Count);
            foreach (var data in referenceList)
            {
                if (hashSet.Add(data.Mvid))
                {
                    list.Add(data);
                }
            }

            return list;
        }
    }
}
