using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Task = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Basic.CompilerLog.Util;

public static class BinaryLogUtil
{
    public static List<CompilerCall> ReadCompilerCalls(Stream stream, List<string> diagnosticList, Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        var list = new List<CompilerCall>();
        var build = BinaryLog.ReadBuild(stream);
        BuildAnalyzer.AnalyzeBuild(build);

        build.VisitAllChildren(
            void (Task task) =>
            {
                if (task is CscTask cscTask)
                {
                    if (TryCreateCompilerCall(cscTask, diagnosticList) is { } cscCall && predicate(cscCall))
                    {
                        list.Add(cscCall);
                    }
                }
                else if (task is VbcTask vbcTask)
                {
                    if (TryCreateCompilerCall(vbcTask, diagnosticList) is { } vbcCall && predicate(vbcCall))
                    {
                        list.Add(vbcCall);
                    }
                }
            });
        return list;
    }

    internal static CompilerCall? TryCreateCompilerCall(CscTask task, List<string> diagnosticList)
    {
        if (FindCompileTarget(task, diagnosticList) is not { } tuple)
        {
            return null;
        }

        var args = CommandLineParser.SplitCommandLineIntoArguments(task.CommandLineArguments, removeHashComments: true);

        var rawArgs = SkipCompilerExecutable(args, "csc.exe", "csc.dll").ToArray();
        if (rawArgs.Length == 0)
        {
            diagnosticList.Add($"Task {task.Id}: bad argument list");
            return null;
        }

        return new CompilerCall(
            tuple.Target.Project.ProjectFile,
            tuple.Kind,
            tuple.Target.Project.TargetFramework,
            isCSharp: true,
            rawArgs,
            index: null);
    }

    internal static CompilerCall? TryCreateCompilerCall(VbcTask task, List<string> diagnosticList)
    {
        if (FindCompileTarget(task, diagnosticList) is not { } tuple)
        {
            return null;
        }

        var args = CommandLineParser.SplitCommandLineIntoArguments(task.CommandLineArguments, removeHashComments: true);
        var rawArgs = SkipCompilerExecutable(args, "vbc.exe", "vbc.dll").ToArray();
        if (rawArgs.Length == 0)
        {
            diagnosticList.Add($"Task {task.Id}: bad argument list");
            return null;
        }

        return new CompilerCall(
            tuple.Target.Project.ProjectFile,
            tuple.Kind,
            tuple.Target.Project.TargetFramework,
            isCSharp: false,
            rawArgs,
            index: null);
    }

    private static (Target Target, CompilerCallKind Kind)? FindCompileTarget(Task task, List<string> diagnosticList)
    {
        var compileTarget = task.GetNearestParent<Target>(static t => t.Name == "CoreCompile" || t.Name == "CoreGenerateSatelliteAssemblies");
        if (compileTarget is null || compileTarget.Project.ProjectDirectory is null)
        {
            diagnosticList.Add($"Task {task.Id}: cannot find CoreCompile");
            return null;
        }

        var kind = compileTarget.Name == "CoreCompile" ? CompilerCallKind.Regular : CompilerCallKind.Satellite;
        return (compileTarget, kind);
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
