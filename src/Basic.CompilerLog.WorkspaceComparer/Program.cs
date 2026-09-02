using Basic.CompilerLog.Util;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Basic.CompilerLog.WorkspaceComparer;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: workspace-compare <project-or-solution> <compiler-log>");
    return 1;
}

var buildPath = Path.GetFullPath(args[0]);
var compilerLogPath = Path.GetFullPath(args[1]);
if (!File.Exists(buildPath))
{
    Console.Error.WriteLine($"Project or solution does not exist: {buildPath}");
    return 1;
}

if (!File.Exists(compilerLogPath))
{
    Console.Error.WriteLine($"Compiler log does not exist: {compilerLogPath}");
    return 1;
}

MSBuildLocator.RegisterDefaults();
using var msbuildWorkspace = MSBuildWorkspace.Create();
msbuildWorkspace.RegisterWorkspaceFailedHandler(e => Console.Error.WriteLine($"MSBuildWorkspace: {e.Diagnostic}"));

Solution expected;
var buildExtension = Path.GetExtension(buildPath);
if (string.Equals(buildExtension, ".sln", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(buildExtension, ".slnx", StringComparison.OrdinalIgnoreCase))
{
    expected = await msbuildWorkspace.OpenSolutionAsync(buildPath);
}
else
{
    expected = (await msbuildWorkspace.OpenProjectAsync(buildPath)).Solution;
}

using var solutionReader = SolutionReader.Create(compilerLogPath);
using var adhocWorkspace = new AdhocWorkspace();
var actual = adhocWorkspace.AddSolution(solutionReader.ReadSolutionInfo());
var differences = WorkspaceComparer.Compare(expected, actual);
foreach (var difference in differences)
{
    Console.WriteLine(difference);
}

Console.Error.WriteLine($"{differences.Length} difference(s)");
return 0;
