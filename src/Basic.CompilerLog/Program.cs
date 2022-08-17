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
        if (extra.Count != 1 || options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        var binlogFilePath = extra[0];
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

int RunResponseFile(IEnumerable<string> args)
{
    var singleLine = false;
    var outputPath = "";
    var options = new FilterOptionSet()
    {
        { "s|singleline", "keep response file as single line",  s => singleLine = s != null },
        { "o|out", "path to output rsp files (default is next to project)", o => outputPath = o },
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

        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = Path.Combine(Environment.CurrentDirectory, ".rsp");
        }

        WriteLine($"Generating response files in {outputPath}");
        Directory.CreateDirectory(outputPath);

        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var responseFileName = GetResponseFileName();
            var responseFilePath = string.IsNullOrEmpty(outputPath)
                ? Path.Combine(Path.GetDirectoryName(compilerCall.ProjectFilePath)!, responseFileName)
                : Path.Combine(outputPath, responseFileName);
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

            string GetResponseFileName()
            {
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
                        name += i.ToString();
                    }
                }

                return name + ".rsp";
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
          rsp           Generate compiler response file for selected projects
          print         Print summary of entries in the log
          help          Print help
        """);
    return ExitFailure;
}

Stream GetOrCreateCompilerLogStream(List<string> extra)
{
    if (extra.Count > 1)
    {
        throw CreateOptionException();
    }

    string? path;
    if (extra.Count == 0)
    {
        path = GetLogFilePath(Environment.CurrentDirectory);
        if (path is null)
        {
            throw CreateOptionException();
        }
    }
    else
    {
        path = extra[0];
    }

    return CompilerLogUtil.GetOrCreateCompilerLogStream(path);

    static string? GetLogFilePath(string baseDirectory)
    {
        // Search the directory for valid log files
        var path = Directory
            .EnumerateFiles(baseDirectory, "*.compilerlog")
            .OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal)
            .FirstOrDefault();
        if (path is not null)
        {
            return path;
        }

        path = Directory
            .EnumerateFiles(baseDirectory, "*.binlog")
            .OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal)
            .FirstOrDefault();
        if (path is not null)
        {
            return path;
        }

        return null;
    }

    static OptionException CreateOptionException() => new("Need a path to a log file", "log");
}



