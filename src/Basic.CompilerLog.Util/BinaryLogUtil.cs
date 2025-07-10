using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Web;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Task = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Basic.CompilerLog.Util;

public static class BinaryLogUtil
{
    // My understanding of the ids in the BuildEventContext type is:
    //
    // 1. Every project evaluation will have a unique evaluation id. Many evaluations of the same
    //    project will occur during a build. Example is a multi-targeted build will have at least
    //    three evaluations: the outer and both inners
    // 2. The project start / stop represent the execution of an evaluated project and it is 
    //    identified with the project context id. The distinction between the two is important
    //    because other events like task started have context ids but no evaluation ids
    //
    //    Note: having a separate context id and evaluation id seems to imply multiple projects
    //    can run within an evaluation but I haven't actually seen that happen.
    // 3. Targets / Tasks have context ids but no evaluation ids. 
    //
    //    Note: it appears that within a context id these ids are unique but have not rigorously
    //    validated that.
    //
    // Oddities observed
    //  - There are project start / stop events that have no evaluation id

    internal sealed class MSBuildProjectContextData(string projectFile, int contextId, int evaluationId)
    {
        private readonly Dictionary<int, CompilationTaskData> _taskMap = new(capacity: 4);
        private readonly Dictionary<int, CompilerCallKind> _targetMap = new(capacity: 4);
        public readonly int ContextId = contextId;
        public int EvaluationId = evaluationId;
        public string? TargetFramework;
        public readonly string ProjectFile = projectFile;

        public bool TryGetTaskData(BuildEventContext context, [NotNullWhen(true)] out CompilationTaskData? data) =>
            _taskMap.TryGetValue(context.TaskId, out data);

        public void SetCompilerCallKind(int targetId, CompilerCallKind kind)
        {
            Debug.Assert(targetId != BuildEventContext.InvalidTargetId);
            _targetMap[targetId] = kind;
        }

        public CompilationTaskData CreateTaskData(BuildEventContext context, bool isCSharp)
        {
            Debug.Assert(!_taskMap.ContainsKey(context.TaskId));
            var data = new CompilationTaskData(context.TargetId, context.TaskId, isCSharp);
            _taskMap[context.TaskId] = data;
            return data;
        }

        public List<CompilerCall> GetAllCompilerCalls(MSBuildProjectEvaluationData? evaluationData, object? ownerState)
        {
            var targetFramework = TargetFramework ?? evaluationData?.TargetFramework;
            var list = new List<CompilerCall>();

            foreach (var data in _taskMap.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value))
            {
                if (!_targetMap.TryGetValue(data.TargetId, out var compilerCallKind))
                {
                    compilerCallKind = CompilerCallKind.Unknown;
                }

                if (data.TryCreateCompilerCall(ProjectFile, targetFramework, compilerCallKind, ownerState) is { } compilerCall)
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

    internal sealed class CompilationTaskData(int targetId, int taskId, bool isCSharp)
    {
        public readonly int TargetId = targetId;
        public readonly int TaskId = taskId;
        public readonly bool IsCSharp = isCSharp;
        public string? CommandLineArguments;

        [ExcludeFromCodeCoverage]
        public override string ToString() => TaskId.ToString();

        internal CompilerCall? TryCreateCompilerCall(string projectFile, string? targetFramework, CompilerCallKind kind, object? ownerState)
        {
            if (CommandLineArguments is null)
            {
                // An evaluation of the project that wasn't actually a compilation
                return null;
            }

            var (compilerFilePath, args) = IsCSharp
                ? ParseTaskForCompilerAndArguments(CommandLineArguments, "csc.exe", "csc.dll")
                : ParseTaskForCompilerAndArguments(CommandLineArguments, "vbc.exe", "vbc.dll");

            return new CompilerCall(
                projectFile,
                compilerFilePath,
                kind,
                targetFramework,
                isCSharp: IsCSharp,
                new Lazy<IReadOnlyCollection<string>>(() => args),
                ownerState: ownerState);
        }
    }

    internal sealed class MSBuildProjectEvaluationData(string projectFile)
    {
        public string ProjectFile = projectFile;
        public string? TargetFramework;

        [ExcludeFromCodeCoverage]
        public override string ToString() => $"{Path.GetFileName(ProjectFile)}({TargetFramework})";
    }

    public static List<CompilerCall> ReadAllCompilerCalls(Stream stream, Func<CompilerCall, bool>? predicate = null, object? ownerState = null)
    {
        var list = new List<CompilerCall>();
        ReadAllCompilerCalls(list, stream, predicate, ownerState);
        return list;
    }

    public static void ReadAllCompilerCalls(List<CompilerCall> list, Stream stream, Func<CompilerCall, bool>? predicate = null, object? ownerState = null)
    {
        // https://github.com/KirillOsenkov/MSBuildStructuredLog/issues/752
        Microsoft.Build.Logging.StructuredLogger.Strings.Initialize();

        predicate ??= static _ => true;
        var records = BinaryLog.ReadRecords(stream);

        var contextMap = new Dictionary<int, MSBuildProjectContextData>();
        var evaluationMap = new Dictionary<int, MSBuildProjectEvaluationData>();

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
                    var contextData = GetOrCreateContextData(context, e.ProjectFile);
                    SetTargetFramework(ref contextData.TargetFramework, e.GlobalProperties);
                    SetTargetFramework(ref contextData.TargetFramework, e.Properties);
                    break;
                }
                case ProjectFinishedEventArgs e:
                {
                    if (contextMap.TryGetValue(context.ProjectContextId, out var contextData))
                    {
                        _ = evaluationMap.TryGetValue(context.EvaluationId, out var evaluationData);
                        foreach (var compilerCall in contextData.GetAllCompilerCalls(evaluationData, ownerState))
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
                    Debug.Assert(context.EvaluationId != BuildEventContext.InvalidEvaluationId);
                    var data = new MSBuildProjectEvaluationData(e.ProjectFile);
                    evaluationMap[context.EvaluationId] = data;
                    break;
                }
                case ProjectEvaluationFinishedEventArgs e:
                {
                    if (evaluationMap.TryGetValue(context.EvaluationId, out var evaluationData))
                    {
                        SetTargetFramework(ref evaluationData.TargetFramework, e.Properties);
                    }
                    break;
                }
                case TargetStartedEventArgs e:
                {
                    Debug.Assert(context.TargetId != BuildEventContext.InvalidTargetId);

                    var callKind = e.TargetName switch
                    {
                        "CoreCompile" when e.ParentTarget == "_CompileTemporaryAssembly" => CompilerCallKind.WpfTemporaryCompile,
                        "CoreCompile" => CompilerCallKind.Regular,
                        "CoreGenerateSatelliteAssemblies" => CompilerCallKind.Satellite,
                        "XamlPreCompile" => CompilerCallKind.XamlPreCompile,
                        _ => (CompilerCallKind?)null
                    };

                    if (callKind is { } ck && contextMap.TryGetValue(context.ProjectContextId, out var contextData))
                    {
                        contextData.SetCompilerCallKind(context.TargetId, ck);
                    }

                    break;
                }
                case TaskStartedEventArgs e:
                {
                    if ((e.TaskName == "Csc" || e.TaskName == "Vbc") &&
                        contextMap.TryGetValue(context.ProjectContextId, out var contextData))
                    {
                        var isCSharp = e.TaskName == "Csc";
                        _ = contextData.CreateTaskData(context, isCSharp);
                    }
                    break;
                }
                case TaskCommandLineEventArgs e:
                {
                    if (contextMap.TryGetValue(context.ProjectContextId, out var contextData) &&
                        contextData.TryGetTaskData(context, out var taskData))
                    {
                        taskData.CommandLineArguments = e.CommandLine;
                    }

                    break;
                }
            }
        }

        MSBuildProjectContextData GetOrCreateContextData(BuildEventContext context, string projecFile)
        {
            if (!contextMap.TryGetValue(context.ProjectContextId, out var contextData))
            {
                contextData = new MSBuildProjectContextData(projecFile, context.ProjectContextId, context.EvaluationId);
                contextMap[context.ProjectContextId] = contextData;
            }

            if (contextData.EvaluationId == BuildEventContext.InvalidEvaluationId)
            {
                contextData.EvaluationId = context.EvaluationId;
            }

            return contextData;
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
                        Debug.Assert(!string.IsNullOrEmpty(property.Value));
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
    internal static (string? CompilerFilePath, string[] Arguments) ParseTaskForCompilerAndArguments(string? args, string exeName, string dllName)
    {
        if (args is null)
        {
            return (null, []);
        }

        var argsStart = 0;
        var appFilePath = FindApplication(args.AsSpan(), ref argsStart, out bool isDotNet);
        if (appFilePath.IsEmpty)
        {
            throw InvalidCommandLine();
        }

        var rawArgs = CommandLineParser.SplitCommandLineIntoArguments(args.Substring(argsStart), removeHashComments: true);
        using var e = rawArgs.GetEnumerator();

        // The path to the executable is not escaped like the other command line arguments. Need
        // to skip until we see an exec or a path with the exe as the file name.
        string? compilerFilePath = null;
        if (isDotNet)
        {
            // The path to the executable is not escaped like the other command line arguments. Need
            // to skip until we see an exec or a path with the exe as the file name.
            while (e.MoveNext())
            {
                if (PathUtil.Comparer.Equals(e.Current, "exec"))
                {
                    if (e.MoveNext() && PathUtil.Comparer.Equals(Path.GetFileName(e.Current), dllName))
                    {
                        compilerFilePath = e.Current;
                    }

                    break;
                }
            }

            if (compilerFilePath is null)
            {
                throw InvalidCommandLine();
            }
        }
        else
        {
            // Direct call to the compiler so we already have the compiler file path in hand
            compilerFilePath = appFilePath.Trim('"').ToString();
        }

        var list = new List<string>();
        while (e.MoveNext())
        {
            list.Add(e.Current);
        }

        return (compilerFilePath, list.ToArray());

        // This search is tricky because there is no attempt by MSBuild to properly quote the 
        ReadOnlySpan<char> FindApplication(ReadOnlySpan<char> args, ref int index, out bool isDotNet)
        {
            isDotNet = false;
            while (index < args.Length && char.IsWhiteSpace(args[index]))
            {
                index++;
            }

            if (index >= args.Length)
            {
                return Span<char>.Empty;
            }

            if (args[index] is '"' or '\'')
            {
                // Quote based parsing, just move to the next quote and return.
                var start = index + 1;
                var quote = args[index];
                do
                {
                    index++;
                }
                while (index < args.Length && args[index] != quote);

                index++; // move past the quote
                var span = args.Slice(start, index - start - 1);
                isDotNet = CheckDotNet(span);
                return span;
            }
            else
            {
                // Move forward until we see a signal that we've reached the compiler 
                // executable.
                //
                // Note: Specifically don't need to handle the case of the application ending at the 
                // exact end of the string. There is always at least one argument to the compiler.
                while (index < args.Length)
                {
                    if (char.IsWhiteSpace(args[index]))
                    {
                        var span = args.Slice(0, index);
                        if (span.EndsWith(exeName.AsSpan()))
                        {
                            isDotNet = false;
                            return span;
                        }

                        if (CheckDotNet(span))
                        {
                            isDotNet = true;
                            return span;
                        }

                        if (span.EndsWith(" exec".AsSpan()))
                        {
                            // This can happen when the dotnet host is not called dotnet. Need to back
                            // up to the path before that.
                            index -= 5;
                            span = args.Slice(0, index);
                            isDotNet = true;
                            return span;
                        }
                    }

                    index++;
                }
            }

            return Span<char>.Empty;

            bool CheckDotNet(ReadOnlySpan<char> span) =>
                span.EndsWith("dotnet".AsSpan()) ||
                span.EndsWith("dotnet.exe".AsSpan());
        }

        Exception InvalidCommandLine() => new InvalidOperationException($"Could not parse command line arguments: {args}");
    }

    /// <summary>
    /// Use <see cref="BinaryLogReader.ReadCommandLineArguments(CompilerCall)"/> instead of this
    /// API whenever possible. That API is more reliable and will throw in cases where the underlying
    /// command line data is unlikely to be present
    /// 
    /// This method is only valid when this instance represents a compilation on the disk of the 
    /// current machine. In any other scenario this will lead to mostly correct but potentially 
    /// incorrect results.
    /// </summary>
    public static CommandLineArguments ReadCommandLineArgumentsUnsafe(CompilerCall compilerCall)
    {
        var baseDirectory = Path.GetDirectoryName(compilerCall.ProjectFilePath)!;
        return compilerCall.IsCSharp
            ? CSharpCommandLineParser.Default.Parse(compilerCall.GetArguments(), baseDirectory, sdkDirectory: null, additionalReferenceDirectories: null)
            : VisualBasicCommandLineParser.Default.Parse(compilerCall.GetArguments(), baseDirectory, sdkDirectory: null, additionalReferenceDirectories: null);
    }
}
