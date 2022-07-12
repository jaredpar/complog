// See https://aka.ms/new-console-template for more information

using Basic.CompilerLogger;

// var filePath = @"c:\users\jaredpar\code\temp\console\msbuild.binlog";
var filePath = @"C:\Users\jaredpar\code\wt\ros2\artifacts\log\Debug\Build.binlog";

using var stream = File.OpenRead(filePath);
var diagnosticList = new List<string>();
BinaryLogUtil.ReadCompilationTasks(stream, diagnosticList);
foreach (var diagnostic in diagnosticList)
{
    Console.WriteLine(diagnostic);
}



