using System.Collections;
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
        public string ProjectFile;
        public string? TargetFramework;
        public string? CommandLineArguments;
        public CompilerCallKind? Kind;
        public int? CompileTaskId;
        public int? EvaluationId;
        public bool IsCSharp;

        public MSBuildProjectData(string projectFile)
        {
            ProjectFile = projectFile;
        }

        public override string ToString() => $"{Path.GetFileName(ProjectFile)}({TargetFramework})";
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
        predicate ??= static _ => true;
        var list = new List<CompilerCall>();
        var records = BinaryLog.ReadRecords(stream);
        var contextMap = new Dictionary<int, MSBuildProjectData>();
        var evaluationMap = new Dictionary<int, MSBuildEvaluationData>();

        foreach (var record in records)
        {
            switch (record.Args)
            {
                case ProjectStartedEventArgs e:
                {
                    var data = GetOrCreateData(e.BuildEventContext, e.ProjectFile);
                    data.EvaluationId = GetEvaluationId(e);
                    SetTargetFramework(ref data.TargetFramework, e.Properties);
                    break;
                }
                case ProjectFinishedEventArgs e:
                {
                    if (contextMap.TryGetValue(e.BuildEventContext.ProjectContextId, out var data))
                    {
                        if (string.IsNullOrEmpty(data.TargetFramework) && 
                            data.EvaluationId is { } evaluationId &&
                            evaluationMap.TryGetValue(evaluationId, out var evaluationData) &&
                            !string.IsNullOrEmpty(evaluationData.TargetFramework))
                        {
                            data.TargetFramework = evaluationData.TargetFramework;
                        }

                        if (TryCreateCompilerCall(data, diagnosticList) is { } compilerCall &&
                            predicate(compilerCall))
                        {
                            list.Add(compilerCall);
                        }
                    }
                    break;
                }
                case ProjectEvaluationStartedEventArgs e:
                {
                    var data = new MSBuildEvaluationData(e.ProjectFile);
                    evaluationMap[e.BuildEventContext.EvaluationId] = data;
                    break;
                }
                case ProjectEvaluationFinishedEventArgs e:
                {
                    if (evaluationMap.TryGetValue(e.BuildEventContext.EvaluationId, out var data))
                    {
                        SetTargetFramework(ref data.TargetFramework, e.Properties);
                    }
                    break;
                }
                case TargetStartedEventArgs e:
                {
                    var callKind = e.TargetName switch
                    {
                        "CoreCompile" => CompilerCallKind.Regular,
                        "CoreGenerateSatelliteAssemblies" => CompilerCallKind.Satellite,
                        _ => (CompilerCallKind?)null
                    };

                    if (callKind is { } ck &&
                        contextMap.TryGetValue(e.BuildEventContext.ProjectContextId, out var data))
                    {
                        data.Kind = ck;
                    }

                    break;
                }
                case TaskStartedEventArgs e:
                {
                    if (e.TaskName == "Csc" || e.TaskName == "Vbc")
                    {
                        if (contextMap.TryGetValue(e.BuildEventContext.ProjectContextId, out var data))
                        {
                            data.CompileTaskId = e.BuildEventContext.TaskId;
                            data.IsCSharp = e.TaskName == "Csc";
                        }
                    }
                    break;
                }
                case TaskCommandLineEventArgs e:
                {
                    if (contextMap.TryGetValue(e.BuildEventContext.ProjectContextId, out var data))
                    {
                        data.CommandLineArguments = e.CommandLine;
                    }

                    break;
                }
            }
        }

        return list;

        static int? GetEvaluationId(ProjectStartedEventArgs e)
        {
            if (e.BuildEventContext is { EvaluationId: > BuildEventContext.InvalidEvaluationId})
            {
                return e.BuildEventContext.EvaluationId;
            }

            if (e.ParentProjectBuildEventContext is { EvaluationId: > BuildEventContext.InvalidEvaluationId})
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

        static CompilerCall? TryCreateCompilerCall(MSBuildProjectData data, List<string> diagnosticList)
        {
            if (data.CommandLineArguments is null)
            {
                // An evaluation of the project that wasn't actually a compilation
                return null;
            }

            if (data.Kind is not {} kind)
            {
                diagnosticList.Add($"Project {data.ProjectFile} ({data.TargetFramework}): cannot find CoreCompile");
                return null;
            }

            var args = CommandLineParser.SplitCommandLineIntoArguments(data.CommandLineArguments, removeHashComments: true);
            var rawArgs = data.IsCSharp 
                ? SkipCompilerExecutable(args, "csc.exe", "csc.dll").ToArray()
                : SkipCompilerExecutable(args, "vbc.exe", "vbc.dll").ToArray();
            if (rawArgs.Length == 0)
            {
                diagnosticList.Add($"Project {data.ProjectFile} ({data.TargetFramework}): bad argument list");
                return null;
            }

            return new CompilerCall(
                data.ProjectFile,
                kind,
                data.TargetFramework,
                isCSharp: data.IsCSharp,
                rawArgs,
                index: null);
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
