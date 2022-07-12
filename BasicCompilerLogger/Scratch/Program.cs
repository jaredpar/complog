// See https://aka.ms/new-console-template for more information

using Basic.CompilerLogger;

using var stream = File.OpenRead(@"c:\users\jaredpar\code\temp\console\msbuild.binlog");
BinaryLogUtil.ReadCompilationTasks(stream, null);
Console.WriteLine("Hello, World!");



