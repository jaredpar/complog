using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Task = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Basic.CompilerLogger;

internal static class BinaryLogUtil
{
    internal static List<CompilerInvocation> ReadCompilationTasks(Stream stream, List<string> diagnosticList)
    {
        var list = new List<CompilerInvocation>();
        var build = BinaryLog.ReadBuild(stream);
        BuildAnalyzer.AnalyzeBuild(build);

        build.VisitAllChildren(
            void (Task task) =>
            {
                if (task is CscTask cscTask)
                {
                    if (TryCreateCompilerInvocation(cscTask, diagnosticList) is { } cscInvocation)
                    {
                        list.Add(cscInvocation);
                    }
                }
                else if (task is VbcTask vbcTask)
                {
                    if (TryCreateCompilerInvocation(vbcTask, diagnosticList) is { } vbcInvocation)
                    {
                        list.Add(vbcInvocation);
                    }
                }
            });
        return list;
    }

    internal static CompilerInvocation? TryCreateCompilerInvocation(CscTask task, List<string> diagnosticList)
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

        var commandLineArgs = CSharpCommandLineParser.Default.Parse(
            rawArgs,
            baseDirectory: tuple.Target.Project.ProjectDirectory,
            sdkDirectory: null,
            additionalReferenceDirectories: null);
        return new CompilerInvocation(
            tuple.Target.Project.ProjectFile,
            task,
            tuple.Kind,
            tuple.Target.Project.TargetFramework,
            commandLineArgs,
            rawArgs);
    }

    internal static CompilerInvocation? TryCreateCompilerInvocation(VbcTask task, List<string> diagnosticList)
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

        var commandLineArgs = VisualBasicCommandLineParser.Default.Parse(
            rawArgs,
            baseDirectory: tuple.Target.Project.ProjectDirectory,
            sdkDirectory: null,
            additionalReferenceDirectories: null);
        return new CompilerInvocation(
            tuple.Target.Project.ProjectFile,
            task,
            tuple.Kind,
            tuple.Target.Project.TargetFramework,
            commandLineArgs,
            rawArgs);
    }

    private static (Target Target, CompilationKind Kind)? FindCompileTarget(Task task, List<string> diagnosticList)
    {
        var compileTarget = task.GetNearestParent<Target>(static t => t.Name == "CoreCompile" || t.Name == "CoreGenerateSatelliteAssemblies");
        if (compileTarget is null || compileTarget.Project.ProjectDirectory is null)
        {
            diagnosticList.Add($"Task {task.Id}: cannot find CoreCompile");
            return null;
        }

        var kind = compileTarget.Name == "CoreCompile" ? CompilationKind.Regular : CompilationKind.Sattelite;
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
            if (StringComparer.OrdinalIgnoreCase.Equals(e.Current, "exec"))
            {
                if (e.MoveNext() && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(e.Current), dllName))
                {
                    found = true;
                }
                break;
            }
            else if (e.Current.EndsWith(exeName, StringComparison.OrdinalIgnoreCase))
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
