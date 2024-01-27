using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Task = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Basic.CompilerLog.Util;

public static class BinaryLogUtil
{
    private sealed class MSBuildProjectData
    {
        private readonly Dictionary<int, CompilationTaskData> _targetMap = new();
        public readonly string ProjectFile;
        public string? TargetFramework;
        public int? EvaluationId;

        public MSBuildProjectData(string projectFile)
        {
            ProjectFile = projectFile;
        }

        public bool TryGetTaskData(int targetId, [NotNullWhen(true)] out CompilationTaskData? data) =>
            _targetMap.TryGetValue(targetId, out data);

        public CompilationTaskData GetOrCreateTaskData(int targetId)
        {
            if (!_targetMap.TryGetValue(targetId, out var data))
            {
                data = new CompilationTaskData(this, targetId);
                _targetMap[targetId] = data;
            }

            return data;
        }

        public List<CompilerCall> GetAllCompilerCalls(List<string> diagnostics)
        {
            var list = new List<CompilerCall>();

            foreach (var data in _targetMap.Values)
            {
                if (data.TryCreateCompilerCall(diagnostics) is { } compilerCall)
                {
                    if (compilerCall.Kind == CompilerCallKind.Regular)
                    {
                        list.Insert(0, compilerCall);
                    }
                    else
                    {
                        list.Add(compilerCall);
                    }
                }
            }

            return list;
        }

        public override string ToString() => $"{Path.GetFileName(ProjectFile)}({TargetFramework})";
    }

    private sealed class CompilationTaskData
    {
        public readonly MSBuildProjectData ProjectData;
        public int TargetId;
        public string? CommandLineArguments;
        public CompilerCallKind? Kind;
        public int? CompileTaskId;
        public bool IsCSharp;

        public string ProjectFile => ProjectData.ProjectFile;
        public string? TargetFramework => ProjectData.TargetFramework;

        public CompilationTaskData(MSBuildProjectData projectData, int targetId)
        {
            ProjectData = projectData;
            TargetId = targetId;
        }

        public override string ToString() => $"{ProjectData} {TargetId}";

        internal CompilerCall? TryCreateCompilerCall(List<string> diagnosticList)
        {
            if (CommandLineArguments is null)
            {
                // An evaluation of the project that wasn't actually a compilation
                return null;
            }

            var kind = Kind ?? CompilerCallKind.Unknown;
            var args = CommandLineParser.SplitCommandLineIntoArguments(CommandLineArguments, removeHashComments: true);
            var rawArgs = IsCSharp
                ? SkipCompilerExecutable(args, "csc.exe", "csc.dll").ToArray()
                : SkipCompilerExecutable(args, "vbc.exe", "vbc.dll").ToArray();
            if (rawArgs.Length == 0)
            {
                diagnosticList.Add($"Project {ProjectFile} ({TargetFramework}): bad argument list");
                return null;
            }

            return new CompilerCall(
                ProjectFile,
                kind,
                TargetFramework,
                isCSharp: IsCSharp,
                new Lazy<string[]>(() => rawArgs),
                index: null);
        }
    }

    private sealed class MSBuildEvaluationData
    {
        public string ProjectFile;
        public string? TargetFramework;

        public MSBuildEvaluationData(string projectFile)
        {
            ProjectFile = projectFile;
        }
        public override string ToString() => $"{Path.GetFileName(ProjectFile)}({TargetFramework})";
    }

    public static List<CompilerCall> ReadAllCompilerCalls(Stream stream, List<string> diagnosticList, Func<CompilerCall, bool>? predicate = null)
    {
        // https://github.com/KirillOsenkov/MSBuildStructuredLog/issues/752
        Microsoft.Build.Logging.StructuredLogger.Strings.Initialize();

        predicate ??= static _ => true;
        var list = new List<CompilerCall>();
        var records = BinaryLog.ReadRecords(stream);
        var contextMap = new Dictionary<int, MSBuildProjectData>();
        var evaluationMap = new Dictionary<int, MSBuildEvaluationData>();

        foreach (var record in records)
        {
            if (record.Args is not { BuildEventContext: { } context })
            {
                continue;
            }

            switch (record.Args)
            {
                case ProjectStartedEventArgs { ProjectFile: not null } e:
                {
                    var data = GetOrCreateData(context, e.ProjectFile);
                    data.EvaluationId = GetEvaluationId(e);
                    SetTargetFramework(ref data.TargetFramework, e.Properties);
                    break;
                }
                case ProjectFinishedEventArgs e:
                {
                    if (contextMap.TryGetValue(context.ProjectContextId, out var data))
                    {
                        if (string.IsNullOrEmpty(data.TargetFramework) &&
                            data.EvaluationId is { } evaluationId &&
                            evaluationMap.TryGetValue(evaluationId, out var evaluationData) &&
                            !string.IsNullOrEmpty(evaluationData.TargetFramework))
                        {
                            data.TargetFramework = evaluationData.TargetFramework;
                        }

                        foreach (var compilerCall in data.GetAllCompilerCalls(diagnosticList))
                        {
                            if (predicate(compilerCall))
                            {
                                list.Add(compilerCall);
                            }
                        }
                    }
                    break;
                }
                case ProjectEvaluationStartedEventArgs { ProjectFile: not null } e:
                {
                    var data = new MSBuildEvaluationData(e.ProjectFile);
                    evaluationMap[context.EvaluationId] = data;
                    break;
                }
                case ProjectEvaluationFinishedEventArgs e:
                {
                    if (evaluationMap.TryGetValue(context.EvaluationId, out var data))
                    {
                        SetTargetFramework(ref data.TargetFramework, e.Properties);
                    }
                    break;
                }
                case TargetStartedEventArgs e:
                {
                    var callKind = e.TargetName switch
                    {
                        "CoreCompile" when e.ParentTarget == "_CompileTemporaryAssembly" => CompilerCallKind.WpfTemporaryCompile,
                        "CoreCompile" => CompilerCallKind.Regular,
                        "CoreGenerateSatelliteAssemblies" => CompilerCallKind.Satellite,
                        "XamlPreCompile" => CompilerCallKind.XamlPreCompile,
                        _ => (CompilerCallKind?)null
                    };

                    if (callKind is { } ck &&
                        context.TargetId != BuildEventContext.InvalidTargetId &&
                        contextMap.TryGetValue(context.ProjectContextId, out var data))
                    {
                        data.GetOrCreateTaskData(context.TargetId).Kind = ck;
                    }

                    break;
                }
                case TaskStartedEventArgs e:
                {
                    if ((e.TaskName == "Csc" || e.TaskName == "Vbc") &&
                        context.TargetId != BuildEventContext.InvalidTargetId &&
                        contextMap.TryGetValue(context.ProjectContextId, out var data))
                    {
                        var taskData = data.GetOrCreateTaskData(context.TargetId);
                        taskData.IsCSharp = e.TaskName == "Csc";
                        taskData.CompileTaskId = context.TaskId;
                    }
                    break;
                }
                case TaskCommandLineEventArgs e:
                {
                    if (context.TargetId != BuildEventContext.InvalidTargetId &&
                        contextMap.TryGetValue(context.ProjectContextId, out var data) &&
                        data.TryGetTaskData(context.TargetId, out var taskData))
                    {
                        taskData.CommandLineArguments = e.CommandLine;
                    }

                    break;
                }
            }
        }

        return list;

        static int? GetEvaluationId(ProjectStartedEventArgs e)
        {
            if (e.BuildEventContext is { EvaluationId: > BuildEventContext.InvalidEvaluationId })
            {
                return e.BuildEventContext.EvaluationId;
            }

            if (e.ParentProjectBuildEventContext is { EvaluationId: > BuildEventContext.InvalidEvaluationId })
            {
                return e.ParentProjectBuildEventContext.EvaluationId;
            }

            return null;
        }

        MSBuildProjectData GetOrCreateData(BuildEventContext context, string projectFile)
        {
            if (!contextMap.TryGetValue(context.ProjectContextId, out var data))
            {
                data = new MSBuildProjectData(projectFile);
                contextMap[context.ProjectContextId] = data;
            }

            return data;
        }

        void SetTargetFramework(ref string? targetFramework, IEnumerable? rawProperties)
        {
            if (rawProperties is not IEnumerable<KeyValuePair<string, string>> properties)
            {
                return;
            }

            foreach (var property in properties)
            {
                switch (property.Key)
                {
                    case "TargetFramework":
                        targetFramework = property.Value;
                        break;
                    case "TargetFrameworks":
                        if (string.IsNullOrEmpty(targetFramework))
                        {
                            targetFramework = property.Value;
                        }
                        break;
                }
            }
        }
    }

    /// <summary>
    /// The argument list is going to include either `dotnet exec csc.dll` or `csc.exe`. Need 
    /// to skip past that to get to the real command line.
    /// </summary>
    internal static IEnumerable<string> SkipCompilerExecutable(IEnumerable<string> args, string exeName, string dllName)
    {
        using var e = args.GetEnumerator();

        // The path to the executable is not escaped like the other command line arguments. Need
        // to skip until we see an exec or a path with the exe as the file name.
        var found = false;
        while (e.MoveNext())
        {
            if (PathUtil.Comparer.Equals(e.Current, "exec"))
            {
                if (e.MoveNext() && PathUtil.Comparer.Equals(Path.GetFileName(e.Current), dllName))
                {
                    found = true;
                }
                break;
            }
            else if (e.Current.EndsWith(exeName, PathUtil.Comparison))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            yield break;
        }

        while (e.MoveNext())
        {
            yield return e.Current;
        }
    }
}
