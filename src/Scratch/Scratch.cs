using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Basic.CompilerLog;
using Basic.CompilerLog.Util;
using BenchmarkDotNet.Environments;
//using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Diagnostics.Tracing.Parsers.JScript;
using Scratch;
using TraceReloggerLib;

#pragma warning disable 8321

PrintGeneratedFiles();

//  var filePath = @"c:\users\jaredpar\temp\console\msbuild.binlog";
// var filePath = @"C:\Users\jaredpar\code\roslyn\artifacts\log\Debug\Build.complog";
// var filePath = @"C:\Users\jaredpar\code\vs-threading\msbuild.binlog";
// var filePath = @"c:\users\jaredpar\temp\Build.complog";
// var filePath = @"C:\Users\jaredpar\code\MudBlazor\src\msbuild.binlog";
// var filePath = @"C:\Users\jaredpar\code\roslyn\src\Compilers\Core\Portable\msbuild.binlog";
// var filePath = @"C:\Users\jaredpar\Downloads\Roslyn.complog";
// var filePath = @"C:\Users\jaredpar\code\wt\ros2\artifacts\log\Debug\Build.binlog";
// var filePath = @"C:\Users\jaredpar\code\roslyn\artifacts\log\Debug\Build.binlog";
//var filePath = @"C:\Users\jaredpar\code\roslyn\src\Compilers\CSharp\csc\msbuild.binlog";

//TestDiagnostics(filePath);
// RoundTrip(filePath);


// await SolutionScratchAsync(filePath);

// Profile();

ExportScratch();
// await WorkspaceScratch();
// RoslynScratch();
// Sarif();
// var timeSpan = DateTime.UtcNow;
// RunComplog($"rsp {filePath} --project Microsoft.VisualStudio.Threading.Tests");
// Console.WriteLine(DateTime.UtcNow - timeSpan);

/*
using var reader = CompilerLogReader.Create(filePath);

VerifyAll(filePath);
Console.WriteLine("Done");
*/

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

static void Test(ITypeSymbol symbol)
{
    if (symbol is not INamedTypeSymbol { IsGenericType: true } nt)
    {
        return;
    }

    Console.WriteLine(nt.Name);
}

static void PrintGeneratedFiles()
{
    var binlogPath = @"C:\Users\jaredpar\code\roslyn\msbuild.binlog";
    using var reader = CompilerLogReader.Create(binlogPath);
    foreach (var cc in reader.ReadAllCompilerCalls())
    {
        var data = reader.ReadRawCompilationData(cc);
        var generatedFiles = data
            .Contents
            .Where(x => x.Kind == RawContentKind.GeneratedText);
        if (!generatedFiles.Any())
        {
            continue;
        }

        Console.WriteLine(cc.GetDiagnosticName());
        foreach (var file in generatedFiles)
        {
            Console.WriteLine($"  {file.FilePath}");
        }
    }
}

static async Task WorkspaceScratch()
{
    var filePath = @"/mnt/c/Users/jaredpar/temp/console/msbuild.complog";
    using var reader = SolutionReader.Create(filePath, CompilerLogReaderOptions.None);
    using var workspace = new AdhocWorkspace();
    workspace.AddSolution(reader.ReadSolutionInfo());
    foreach (var project in workspace.CurrentSolution.Projects)
    {
        foreach (var document in project.Documents)
        {
            var text = await document.GetTextAsync();
            var textSpan = new TextSpan(0, text.Length);
            _ = await Classifier.GetClassifiedSpansAsync(document, textSpan);
        }
    }
}

static void ExportScratch()
{
    var filePath = @"/mnt/c/Users/jaredpar/temp/console/msbuild.complog";
    var dest = "/home/jaredpar/temp/export";
    if (Directory.Exists(dest))
    {
        Directory.Delete(dest, recursive: true);
    }

    using var reader = CompilerLogReader.Create(filePath, CompilerLogReaderOptions.None);
    var exportUtil = new ExportUtil(reader);
    exportUtil.ExportAll(dest, SdkUtil.GetSdkDirectories());
}

static void RoslynScratch()
{
    var code = """
        using System;
        public class C {
            public void M() {
        #line (7,2)-(7,6) 24 "C:\SomeProject\SomeRazorFile.razor"
        Console.WriteLine("hello world");
            }
        }

        """;

    var syntaxTree = CSharpSyntaxTree.ParseText(code);
    var compilation = CSharpCompilation.Create(
        "scratch",
        new[] { syntaxTree },
        Basic.Reference.Assemblies.Net60.All);

    var context = compilation.GetSemanticModel(syntaxTree);
    var token = syntaxTree
        .GetRoot()
        .DescendantTokens()
        .First(x => x.Text == "Console");

    /*
    var node = syntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Single();
    var cType = context.GetDeclaredSymbol(node);
    var enumerableType = compilation.GetSpecialType(SpecialType.System_Collections_IEnumerable);
    var conversion = compilation.ClassifyConversion(cType!, enumerableType);
    Console.WriteLine(conversion.Exists);
    */


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

static void Sarif()
{
    var binlogPath = @"C:\Users\jaredpar\Downloads\BlazorWebApp\BlazorWebApp\msbuild.binlog";
    using var reader = CompilerLogReader.Create(binlogPath);
    var cc = reader.ReadAllCompilerCalls().Single(x => x.ProjectFilePath.Contains("BlazorWebApp.csproj"));
    var data = reader.ReadCompilationData(cc);
    var diagnostics = data.GetAllDiagnosticsAsync().GetAwaiter().GetResult();
    foreach (var d in diagnostics)
    {
        Console.WriteLine(d.Location.GetLineSpan());
        Console.WriteLine(d.Location.GetMappedLineSpan());
    }
}

void Profile()
{
    var binlogPath = @"c:\users\jaredpar\temp\console\msbuild.binlog";
    var complogPath = @"c:\users\jaredpar\temp\console\msbuild.complog";

    if (File.Exists(complogPath))
    {
        File.Delete(complogPath);
    }

    _ = CompilerLogUtil.ConvertBinaryLog(binlogPath, complogPath);

    using var reader = CompilerLogReader.Create(complogPath);
    foreach (var compilerCall in reader.ReadAllCompilerCalls())
    {
        _ = reader.ReadCompilationData(compilerCall);
    }
}

int RunComplog(string args)
{
    var assembly = typeof(FilterOptionSet).Assembly;
    var program = assembly.GetType("Program", throwOnError: true);
    var main = program!.GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic);
    return (int)main!.Invoke(null, new[] { args.Split(' ', StringSplitOptions.RemoveEmptyEntries) })!;
}

void VerifyAll(string logPath)
{
    var exportDest = @"c:\users\jaredpar\temp\export";
    RunComplog($"replay {logPath} -o {exportDest} -export");
}

void ExportTest(CompilerLogReader reader)
{
    var dest = @"c:\users\jaredpar\temp\export";
    EmptyDirectory(dest);

    var d = SdkUtil.GetSdkDirectories();
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

static async System.Threading.Tasks.Task SolutionScratchAsync(string binlogFilePath)
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




