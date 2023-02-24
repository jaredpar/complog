using Basic.CompilerLog;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

#pragma warning disable 8321

// var filePath = @"c:\users\jaredpar\temp\console\msbuild.binlog";
var filePath = @"C:\Users\jaredpar\code\roslyn\src\Compilers\Core\Portable\msbuild.binlog";
// var filePath = @"C:\Users\jaredpar\code\wt\ros2\artifacts\log\Debug\Build.binlog";
// var filePath = @"C:\Users\jaredpar\code\roslyn\artifacts\log\Debug\Build.binlog";
//var filePath = @"C:\Users\jaredpar\code\roslyn\src\Compilers\CSharp\csc\msbuild.binlog";

//TestDiagnostics(filePath);
// RoundTrip(filePath);


// await SolutionScratchAsync(filePath);

Scratch();

void Scratch()
{
    var code = """
        using System.Reflection;
        class C
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
}


void ExportTest()
{
    var dest = @"c:\users\jaredpar\temp\export";
    EmptyDirectory(dest);

    var d = DotnetUtil.GetSdkDirectories();
    var reader = CompilerLogReader.Create(filePath!);
    var util = new ExportUtil(reader);
    util.ExportRsp(reader.ReadCompilerCall(0), dest, DotnetUtil.GetSdkDirectories());
}

void EmptyDirectory(string directory)
{
    if (Directory.Exists(directory))
    {
        var d = new DirectoryInfo(directory);
        foreach(System.IO.FileInfo file in d.GetFiles()) file.Delete();
        foreach(System.IO.DirectoryInfo subDirectory in d.GetDirectories()) subDirectory.Delete(true);
    }
}

void RoundTrip(string binlogFilePath)
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

async Task SolutionScratchAsync(string binlogFilePath)
{
    using var stream = CompilerLogUtil.GetOrCreateCompilerLogStream(binlogFilePath);
    using var reader = SolutionReader.Create(stream, leaveOpen: false);
    var solution = reader.ReadSolution();
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

void TestDiagnostics(string binlogFilePath)
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




