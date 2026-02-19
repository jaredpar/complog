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

    internal ICompilerCallReader Reader { get; }
    internal VersionStamp VersionStamp { get; }
    internal SolutionId SolutionId { get; } = SolutionId.CreateNewId();

    public int ProjectCount => _indexToProjectDataMap.Count;

    internal SolutionReader(ICompilerCallReader reader, Func<CompilerCall, bool>? predicate = null, VersionStamp? versionStamp = null)
    {
        Reader = reader;
        VersionStamp = versionStamp ?? VersionStamp.Default;

        predicate ??= static c => c.Kind == CompilerCallKind.Regular;
        var map = new SortedDictionary<int, (CompilerCall, ProjectId)>();
        var compilerCalls = reader.ReadAllCompilerCalls();
        for (int i = 0; i < compilerCalls.Count; i++)
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
        var reader = CompilerCallReaderUtil.Create(filePath, basicAnalyzerKind, state);
        return new(reader, predicate);
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
        var documents = new List<DocumentInfo>();
        var additionalDocuments = new List<DocumentInfo>();
        var analyzerConfigDocuments = new List<DocumentInfo>();

        foreach (var sourceTextData in Reader.ReadAllSourceTextData(compilerCall))
        {
            List<DocumentInfo> list = sourceTextData.SourceTextKind switch
            {
                SourceTextKind.SourceCode => documents,
                SourceTextKind.AnalyzerConfig => analyzerConfigDocuments,
                SourceTextKind.AdditionalText => additionalDocuments,
                _ => throw new InvalidOperationException(),
            };

            var fileName = sourceTextData.FilePath;
            var documentId = DocumentId.CreateNewId(projectId, debugName: fileName);
            list.Add(DocumentInfo.Create(
                documentId,
                fileName,
                loader: new CompilerLogTextLoader(Reader, VersionStamp, sourceTextData),
                filePath: sourceTextData.FilePath));
        }

        var refTuple = ReadReferences();
        var compilerCallData = Reader.ReadCompilerCallData(compilerCall);
        var basicAnalyzeHost = Reader.CreateBasicAnalyzerHost(compilerCall);
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp,
            name: compilerCall.ProjectFileName,
            assemblyName: compilerCallData.AssemblyFileName,
            language: compilerCall.IsCSharp ? LanguageNames.CSharp : LanguageNames.VisualBasic,
            filePath: compilerCall.ProjectFilePath,
            outputFilePath: Path.Combine(compilerCallData.OutputDirectory ?? "", compilerCallData.AssemblyFileName),
            compilationOptions: compilerCallData.CompilationOptions,
            parseOptions: compilerCallData.ParseOptions,
            documents,
            refTuple.Item1,
            refTuple.Item2,
            analyzerReferences: basicAnalyzeHost.AnalyzerReferences,
            additionalDocuments,
            isSubmission: false,
            hostObjectType: null);

        return projectInfo.WithAnalyzerConfigDocuments(analyzerConfigDocuments);

        (List<ProjectReference>, List<MetadataReference>) ReadReferences()
        {
            // The command line compiler supports having the same reference added multiple times. It's actually
            // not uncommon for Microsoft.VisualBasic.dll to be passed twice when working on Visual Basic projects. 
            // The workspaces layer though cannot handle duplicates hence we need to run a de-dupe pass here.
            var hashSet = new HashSet<Guid>();
            var projectReferences = new List<ProjectReference>();
            var metadataReferences = new List<MetadataReference>();
            foreach (var referenceData in Reader.ReadAllReferenceData(compilerCall))
            {
                if (referenceData.IsImplicit)
                {
                    continue;
                }

                if (!hashSet.Add(referenceData.Mvid))
                {
                    continue;
                }

                if (Reader.TryGetCompilerCallIndex(referenceData.Mvid, out var refCompilerCallIndex))
                {
                    var refProjectId = _indexToProjectDataMap[refCompilerCallIndex].ProjectId;
                    projectReferences.Add(new ProjectReference(refProjectId, referenceData.Aliases, referenceData.EmbedInteropTypes));
                }
                else
                {
                    var metadataReference = Reader.ReadMetadataReference(referenceData);
                    metadataReferences.Add(metadataReference);
                }
            }

            return (projectReferences, metadataReferences);
        }
    }
}
