using Basic.CompilerLog;
using Basic.CompilerLog.Util;
using Microsoft.Build.Tasks.Deployment.ManifestUtilities;
using Microsoft.CodeAnalysis;
using Mono.Options;
using System.Runtime.Loader;
using System.Text;
using static Constants;
using static System.Console;

var (command, rest) = args.Length == 0
    ? ("help", Enumerable.Empty<string>())
    : (args[0], args.Skip(1));

try
{
    return command.ToLower() switch
    {
        "create" => RunCreate(rest),
        "diagnostics" => RunDiagnostics(rest),
        "export" => RunExport(rest),
        "ref" => RunReferences(rest),
        "rsp" => RunResponseFile(rest),
        "print" => RunPrint(rest),
        "help" => RunHelp(),
        _ => RunHelp()
    };
}
catch (Exception e)
{
    RunHelp();
    WriteLine("Unexpected error");
    WriteLine(e.Message);
    return ExitFailure;
}

int RunCreate(IEnumerable<string> args)
{
    var options = new FilterOptionSet();

    try
    {
        var extra = options.Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        string? binlogFilePath = null;
        if (extra.Count == 1)
        {
            binlogFilePath = extra[0];
        }
        else if (extra.Count == 0)
        {
            binlogFilePath = Directory
                .EnumerateFiles(CurrentDirectory, "*.binlog")
                .OrderBy(x => Path.GetFileName(x), PathUtil.Comparer)
                .FirstOrDefault();
        }

        if (binlogFilePath is null)
        {
            PrintUsage();
            return ExitFailure;
        }

        var compilerLogFileName = $"{Path.GetFileNameWithoutExtension(binlogFilePath)}.compilerlog";
        var compilerLogFilePath = Path.Combine(Path.GetDirectoryName(binlogFilePath)!, compilerLogFileName);
        var diagnosticList = CompilerLogUtil.ConvertBinaryLog(
            binlogFilePath,
            compilerLogFilePath,
            options.FilterCompilerCalls);

        foreach (var diagnostic in diagnosticList)
        {
           WriteLine(diagnostic);
        }

        return ExitSuccess;
    }
    catch (OptionException e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("compilerlog create [OPTIONS] binlog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunPrint(IEnumerable<string> args)
{
    var options = new FilterOptionSet();

    try
    {
        var extra = options.Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        using var compilerLogStream = GetOrCreateCompilerLogStream(extra);
        var compilerCalls = CompilerLogUtil.ReadCompilerCalls(
            compilerLogStream,
            options.FilterCompilerCalls);

        foreach (var compilerCall in compilerCalls)
        {
            Write($"{compilerCall.ProjectFilePath} ({compilerCall.TargetFramework})");
            if (compilerCall.Kind == CompilerCallKind.Satellite)
            {
                Write(" (satellite)");
            }
            WriteLine();
        }

        return ExitSuccess;
    }
    catch (OptionException e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("compilerlog print [OPTIONS] build.compilerlog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunReferences(IEnumerable<string> args)
{
    var outputPath = "";
    var options = new FilterOptionSet()
    {
        { "o|out", "path to output reference files", o => outputPath = o },
    };

    try
    {
        var extra = options.Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        using var compilerLogStream = GetOrCreateCompilerLogStream(extra);
        using var reader = CompilerLogReader.Create(compilerLogStream, leaveOpen: true);
        var compilerCalls = reader.ReadCompilerCalls(options.FilterCompilerCalls);

        outputPath = GetOutputPath(outputPath, "refs");
        WriteLine($"Copying references to {outputPath}");
        Directory.CreateDirectory(outputPath);

        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var refDirPath = Path.Combine(outputPath, GetProjectUniqueName(compilerCalls, i));
            Directory.CreateDirectory(refDirPath);
            var referenceInfoList = reader.ReadReferenceFileInfo(compilerCall);
            foreach (var tuple in referenceInfoList)
            {
                var filePath = Path.Combine(refDirPath, tuple.FileName);
                File.WriteAllBytes(filePath, tuple.ImageBytes.ToArray());
            }
        }

        return ExitSuccess;
    }
    catch (OptionException e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("compilerlog rsp [OPTIONS] build.compilerlog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunExport(IEnumerable<string> args)
{
    var outputPath = "";
    var options = new FilterOptionSet()
    {
        { "o|out=", "path to output rsp files", o => outputPath = o },
    };

    try
    {
        var extra = options.Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        using var compilerLogStream = GetOrCreateCompilerLogStream(extra);
        using var reader = CompilerLogReader.Create(compilerLogStream, leaveOpen: true);
        var compilerCalls = reader.ReadCompilerCalls(options.FilterCompilerCalls);
        var exportUtil = new ExportUtil(reader);

        outputPath = GetOutputPath(outputPath, "export");
        WriteLine($"Exporting to {outputPath}");
        Directory.CreateDirectory(outputPath);

        var sdkDirs = DotnetUtil.GetSdkDirectories();
        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var exportDirName = GetProjectUniqueName(compilerCalls, i);
            var exportDir = Path.Combine(outputPath, exportDirName);
            exportUtil.ExportRsp(compilerCall, exportDir, sdkDirs);
        }

        return ExitSuccess;
    }
    catch (OptionException e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("compilerlog export [OPTIONS] build.compilerlog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunResponseFile(IEnumerable<string> args)
{
    var singleLine = false;
    var outputPath = "";
    var options = new FilterOptionSet()
    {
        { "s|singleline", "keep response file as single line",  s => singleLine = s != null },
        { "o|out=", "path to output rsp files", o => outputPath = o },
    };

    try
    {
        var extra = options.Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        using var compilerLogStream = GetOrCreateCompilerLogStream(extra);
        var compilerCalls = CompilerLogUtil.ReadCompilerCalls(
            compilerLogStream,
            options.FilterCompilerCalls);

        outputPath = GetOutputPath(outputPath, "rsp");
        WriteLine($"Generating response files in {outputPath}");
        Directory.CreateDirectory(outputPath);

        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var responseFileName = GetProjectUniqueName(compilerCalls, i) + ".rsp";
            var responseFilePath = Path.Combine(outputPath, responseFileName);
            using var writer = new StreamWriter(responseFilePath, append: false, Encoding.UTF8);
            if (singleLine)
            {
                writer.WriteLine(string.Join(' ', compilerCall.Arguments));
            }
            else
            {
                foreach (var arg in compilerCall.Arguments)
                {
                    writer.WriteLine(arg);
                }
            }
        }

        return ExitSuccess;
    }
    catch (OptionException e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("compilerlog rsp [OPTIONS] build.compilerlog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunDiagnostics(IEnumerable<string> args)
{
    var severity = DiagnosticSeverity.Warning;
    var options = new FilterOptionSet()
    {
        { "severity", "minimum severity to display (default Warning)", (DiagnosticSeverity s) => severity = s },
    };

    try
    {
        var extra = options.Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        using var compilerLogStream = GetOrCreateCompilerLogStream(extra);
        var compilationDatas = CompilerLogUtil.ReadCompilationDatas(
            compilerLogStream,
            options.FilterCompilerCalls);

        foreach (var compilationData in compilationDatas)
        {
            var compilerCall = compilationData.CompilerCall;
            WriteLine($"{compilerCall.ProjectFileName} ({compilerCall.TargetFramework})");
            var compilation = compilationData.GetCompilationAfterGenerators();
            foreach (var diagnostic in compilation.GetDiagnostics())
            {
                if (diagnostic.Severity >= severity)
                {
                    WriteLine(diagnostic.GetMessage());
                }
            }
        }

        return ExitSuccess;
    }
    catch (OptionException e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("compilerlog diagnostics [OPTIONS] compilerlog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunHelp()
{
    WriteLine("""
        compilerlog [command] [args]
        Commands
          create        Create a compilerlog file 
          diagnostics   Print diagnostics for a compilation
          export        Export a complete project to disk
          rsp           Generate compiler response file for selected projects
          ref           Copy all references to a single directory
          print         Print summary of entries in the log
          help          Print help
        """);
    return ExitFailure;
}

Stream GetOrCreateCompilerLogStream(List<string> extra)
{
    var logFilePath = GetLogFilePath(extra);
    return CompilerLogUtil.GetOrCreateCompilerLogStream(logFilePath);
}

string GetLogFilePath(List<string> extra)
{
    if (extra.Count > 1)
    {
        throw CreateOptionException();
    }

    string? path;
    if (extra.Count == 0)
    {
        path = GetLogFilePath(CurrentDirectory);
    }
    else
    {
        path = extra[0];
        if (string.IsNullOrEmpty(Path.GetExtension(path)))
        {
            path = GetLogFilePath(path);
        }
    }

    return path;

    static string GetLogFilePath(string baseDirectory)
    {
        // Search the directory for valid log files
        var path = Directory
            .EnumerateFiles(baseDirectory, "*.compilerlog")
            .OrderBy(x => Path.GetFileName(x), PathUtil.Comparer)
            .FirstOrDefault();
        if (path is not null)
        {
            return path;
        }

        path = Directory
            .EnumerateFiles(baseDirectory, "*.binlog")
            .OrderBy(x => Path.GetFileName(x), PathUtil.Comparer)
            .FirstOrDefault();
        if (path is not null)
        {
            return path;
        }

        throw CreateOptionException();
    }

    static OptionException CreateOptionException() => new("Need a path to a log file", "log");
}

string GetOutputPath(string? outputPath, string directoryName)
{
    if (!string.IsNullOrEmpty(outputPath))
    {
        return outputPath;
    }

    return Path.Combine(CurrentDirectory, ".compilerlog", directoryName);
}

string GetProjectUniqueName(List<CompilerCall> compilerCalls, int index)
{
    var compilerCall = compilerCalls[index];
    var name = Path.GetFileNameWithoutExtension(compilerCall.ProjectFileName);

    // If the project is built multiple times then need to make it unique
    if (compilerCalls.Count(x => x.ProjectFilePath == compilerCall.ProjectFilePath) > 1)
    {
        if (!string.IsNullOrEmpty(compilerCall.TargetFramework))
        {
            name += "-" + compilerCall.TargetFramework;
        }
        else
        {
            name += index.ToString();
        }
    }

    return name;
}





