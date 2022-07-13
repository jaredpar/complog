// See https://aka.ms/new-console-template for more information

using Basic.CompilerLogger;

var filePath = @"c:\users\jaredpar\temp\console\msbuild.binlog";
// var filePath = @"C:\Users\jaredpar\code\wt\ros2\artifacts\log\Debug\Build.binlog";
// var filePath = @"C:\Users\jaredpar\code\roslyn\artifacts\log\Debug\Build.binlog";

RoundTrip(filePath);


void RoundTrip(string binlogFilePath)
{
    var compilerLogFilePath = @"c:\users\jaredpar\temp\compiler.zip";
    using var stream = File.OpenRead(binlogFilePath);
    var diagnosticList = new List<string>();

    CompilerLogUtil.WriteTo(compilerLogFilePath, binlogFilePath, diagnosticList);
    foreach (var diagnostic in diagnosticList)
    {
        Console.WriteLine(diagnostic);
    }

    using var compilerLogStream = new FileStream(compilerLogFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    var reader = new CompilerLogReader(compilerLogStream);
    var compilation = reader.ReadCompilation(0);
    compilation.GetAnalyzers();
}




