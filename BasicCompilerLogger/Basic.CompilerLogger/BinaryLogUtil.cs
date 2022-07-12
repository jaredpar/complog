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
        build.VisitAllChildren(
            void (Task task) =>
            {
                if (task is CscTask cscTask && TryCreateCompilerInvocation(cscTask) is { } cscInvocation)
                {
                    list.Add(cscInvocation);
                }
                else if (task is VbcTask vbcTask && TryCreateCompilerInvocation(vbcTask) is { } vbcInvocation)
                {
                    list.Add(vbcInvocation);
                }
            });
        return list;
    }

    internal static CompilerInvocation? TryCreateCompilerInvocation(CscTask task)
    {
        var compileTarget = task.GetNearestParent<Target>(static t => t.Name == "CoreCompile");
        if (compileTarget is null || compileTarget.Project.ProjectDirectory is null)
        {
            return null;
        }

        var args = CommandLineParser.SplitCommandLineIntoArguments(task.CommandLineArguments, removeHashComments: true);
        var rawArgs = SkipCompilerExecutable(args, "csc.exe", "csc.dll").ToArray();
        if (rawArgs.Length == 0)
        {
            return null;
        }

        var commandLineArgs = CSharpCommandLineParser.Default.Parse(
            rawArgs,
            baseDirectory: compileTarget.Project.ProjectDirectory,
            sdkDirectory: null,
            additionalReferenceDirectories: null);
        return new CompilerInvocation(
            compileTarget.Project.ProjectFile,
            task,
            commandLineArgs,
            rawArgs);
    }

    internal static CompilerInvocation? TryCreateCompilerInvocation(VbcTask task)
    {
        var compileTarget = task.GetNearestParent<Target>(static t => t.Name == "CoreCompile");
        if (compileTarget is null || compileTarget.Project.ProjectDirectory is null)
        {
            return null;
        }

        var args = CommandLineParser.SplitCommandLineIntoArguments(task.CommandLineArguments, removeHashComments: true);
        var rawArgs = SkipCompilerExecutable(args, "vbc.exe", "vbc.dll").ToArray();
        if (rawArgs.Length == 0)
        {
            return null;
        }

        var commandLineArgs = VisualBasicCommandLineParser.Default.Parse(
            rawArgs,
            baseDirectory: compileTarget.Project.ProjectDirectory,
            sdkDirectory: null,
            additionalReferenceDirectories: null);
        return new CompilerInvocation(
            compileTarget.Project.ProjectFile,
            task,
            commandLineArgs,
            rawArgs);
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
