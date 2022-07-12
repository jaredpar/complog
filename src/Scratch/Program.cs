// See https://aka.ms/new-console-template for more information

using Basic.CompilerLogger;

// var filePath = @"c:\users\jaredpar\temp\console\msbuild.binlog";
// var filePath = @"C:\Users\jaredpar\code\wt\ros2\artifacts\log\Debug\Build.binlog";
var filePath = @"C:\Users\jaredpar\code\roslyn\artifacts\log\Debug\Build.binlog";

using var stream = File.OpenRead(filePath);
var diagnosticList = new List<string>();

CompilerLogUtil.WriteTo(@"c:\users\jaredpar\temp\compiler.zip", filePath, diagnosticList);
// BinaryLogUtil.ReadCompilationTasks(stream, diagnosticList);
foreach (var diagnostic in diagnosticList)
{
    Console.WriteLine(diagnostic);
}



