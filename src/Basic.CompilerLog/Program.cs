using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Mono.Options;
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
        "print" => RunPrint(rest),
        "help" => RunHelp(),
        _ => RunHelp()
    };
}
catch (Exception e)
{
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
        if (extra.Count != 1 || options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        using var compilerLogStream = CompilerLogUtil.GetOrCreateCompilerLogStream(extra[0]);
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
        WriteLine("compilerlog print [OPTIONS] compilerlog");
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
        if (extra.Count != 1 || options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        using var compilerLogStream = CompilerLogUtil.GetOrCreateCompilerLogStream(extra[0]);
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
          print         Print summary of entries in the log
          help          Print help
        """);
    return ExitFailure;
}


