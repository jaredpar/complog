using System.Diagnostics;
using Basic.CompilerLog;
using Basic.CompilerLog.Util;
using BenchmarkDotNet.Environments;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Diagnostics.Tracing.Parsers.JScript;
using Scratch;
using TraceReloggerLib;

#pragma warning disable 8321

// var filePath = @"c:\users\jaredpar\temp\console\msbuild.binlog";
var filePath = @"c:\users\jaredpar\temp\Build.complog";
// var filePath = @"C:\Users\jaredpar\code\MudBlazor\src\msbuild.binlog";
// var filePath = @"C:\Users\jaredpar\code\roslyn\src\Compilers\Core\Portable\msbuild.binlog";
// var filePath = @"C:\Users\jaredpar\Downloads\Roslyn.complog";
// var filePath = @"C:\Users\jaredpar\code\wt\ros2\artifacts\log\Debug\Build.binlog";
// var filePath = @"C:\Users\jaredpar\code\roslyn\artifacts\log\Debug\Build.binlog";
//var filePath = @"C:\Users\jaredpar\code\roslyn\src\Compilers\CSharp\csc\msbuild.binlog";

//TestDiagnostics(filePath);
// RoundTrip(filePath);


// await SolutionScratchAsync(filePath);

using var reader = CompilerLogReader.Create(filePath);

VerifyAll(filePath);
Console.WriteLine("Done");

/*
var reader = SolutionReader.Create(filePath);
var info = reader.ReadSolutionInfo();
var workspace = new AdhocWorkspace();
workspace.AddSolution(info);
*/


/*
var b = new CompilerBenchmark()
{
    Options = BasicAnalyzersOptions.InMemory
};
b.GenerateLog();
b.LoadAnalyzers();
*/
//_ = BenchmarkDotNet.Running.BenchmarkRunner.Run<CompilerBenchmark>();

/*
var binlogPath = @"c:\users\jaredpar\temp\console\msbuild.binlog";
var complogPath = Path.Combine(Path.GetDirectoryName(binlogPath)!, "msbuild.complog");
if (File.Exists(complogPath))
{
    File.Delete(complogPath);
}
CompilerLogUtil.ConvertBinaryLog(binlogPath, complogPath);
var reader = CompilerLogReader.Create(complogPath);
var analyzers = reader.ReadBasicAnalyzers(reader.ReadRawCompilationData(0).Item2.Analyzers, BasicAnalyzersOptions.InMemory);
foreach (var analyzer in analyzers.AnalyzerReferences)
{
    _ = analyzer.GetAnalyzersForAllLanguages();
    _ = analyzer.GetGeneratorsForAllLanguages();
}
*/

static void RoslynScratch()
{
    var code = """
        using System.Reflection;
        sealed class C
        {
           static void C(Assembly assembly) { }
        }
        """;

    var syntaxTree = CSharpSyntaxTree.ParseText(code);
    var compilation = CSharpCompilation.Create(
        "scratch",
        new[] { syntaxTree },
        Basic.Reference.Assemblies.Net60.All);

    var context = compilation.GetSemanticModel(syntaxTree);
    var node = syntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
    var cType = context.GetDeclaredSymbol(node);
    var enumerableType = compilation.GetSpecialType(SpecialType.System_Collections_IEnumerable);
    var conversion = compilation.ClassifyConversion(cType!, enumerableType);
    Console.WriteLine(conversion.Exists);


    /*
    var node = syntaxTree.GetRoot().DescendantNodes().OfType<ParameterSyntax>().Single();
    var format = SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);
    if (context.GetDeclaredSymbol(node) is IParameterSymbol { Type: var type })
    {
        Console.WriteLine(type.ToDisplayString(format));
    }
    else
    {
        // error resolving parameter, possible errors in the compilation
    }
    */

}

void VerifyAll(string logPath, BasicAnalyzerHostOptions? options = null)
{
    var exportDest = @"c:\users\jaredpar\temp\export";
    EmptyDirectory(exportDest);

    options ??= BasicAnalyzerHostOptions.None;
    using var reader = CompilerLogReader.Create(logPath, options);
    var exportUtil = new ExportUtil(reader, includeAnalyzers: options.Value.Kind != BasicAnalyzerKind.None);
    var sdkDirs = DotnetUtil.GetSdkDirectories();
    int failedCount = 0;
    foreach (var compilationData in reader.ReadAllCompilationData())
    {
        Console.Write($"{compilationData.CompilerCall.GetDiagnosticName()} ...");
        var result = compilationData.EmitToMemory();
        if (result.Success)
        {
            Console.WriteLine("Success");
            continue;
        }

        Console.WriteLine("Error");
        foreach (var item in result.Diagnostics)
        {
            Console.WriteLine(item.GetMessage());
        }

        var dest = Path.Combine(exportDest, failedCount.ToString());
        Console.WriteLine($"Exporting to {dest}");
        exportUtil.Export(compilationData.CompilerCall, dest, sdkDirs);
        failedCount++;
    }
}

void ExportTest(CompilerLogReader reader)
{
    var dest = @"c:\users\jaredpar\temp\export";
    EmptyDirectory(dest);

    var d = DotnetUtil.GetSdkDirectories();
    var util = new ExportUtil(reader);
    util.ExportAll(dest, d);
}

static void EmptyDirectory(string directory)
{
    if (Directory.Exists(directory))
    {
        var d = new DirectoryInfo(directory);
        foreach(System.IO.FileInfo file in d.GetFiles()) file.Delete();
        foreach(System.IO.DirectoryInfo subDirectory in d.GetDirectories()) subDirectory.Delete(true);
    }
}

static void RoundTrip(string binlogFilePath)
{
    var compilerLogFilePath = @"c:\users\jaredpar\temp\compiler.zip";
    using var stream = File.OpenRead(binlogFilePath);
    var diagnosticList = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, compilerLogFilePath);
    foreach (var diagnostic in diagnosticList)
    {
        Console.WriteLine(diagnostic);
    }

    using var compilerLogStream = new FileStream(compilerLogFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    using var reader = CompilerLogReader.Create(compilerLogStream);
    for (int i = 0; i < reader.Count; i++)
    {
        var compilerCall = reader.ReadCompilerCall(i);
        Console.WriteLine($"{compilerCall.ProjectFilePath} ({compilerCall.TargetFramework})");

        var compilation = reader.ReadCompilationData(compilerCall);
    }
}

static async Task SolutionScratchAsync(string binlogFilePath)
{
    using var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(binlogFilePath);
    using var reader = SolutionReader.Create(stream, leaveOpen: false);
    var solution = reader.ReadSolutionInfo();
    var workspace = new AdhocWorkspace();
    workspace.AddSolution(solution);

    foreach (var project in workspace.CurrentSolution.Projects)
    {
        var compilation = await project.GetCompilationAsync();
        foreach (var syntaxTree in compilation!.SyntaxTrees)
        {
            Console.WriteLine(syntaxTree.ToString());
        }
    }
}

static void TestDiagnostics(string binlogFilePath)
{
    using var compilerLogStream = CompilerLogUtil.GetOrCreateCompilerLogStream(binlogFilePath); 
    using var reader = CompilerLogReader.Create(compilerLogStream);
    for (int i = 0; i < reader.Count; i++)
    {
        var compilationData = reader.ReadCompilationData(i);
        var compilation = compilationData.GetCompilationAfterGenerators();
        var diagnostics = compilation.GetDiagnostics().Where(x => x.Severity >= DiagnosticSeverity.Warning);
        foreach (var d in diagnostics)
        {
            Console.WriteLine(d.GetMessage());
        }
    }
}




