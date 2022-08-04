using Basic.CompilerLog.Util;
using Mono.Options;
using static Constants;
using static System.Console;

var (command, rest) = args.Length == 0
    ? ("help", Enumerable.Empty<string>())
    : (args[0], args.Skip(1));

try
{
    return command switch
    {
        "create" => RunCreate(rest),
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
    var help = false;
    var options = new FilterOptionSet
    {
        { "h|help", "print help", h => { if (h != null) help = true; } },
    };

    try
    {
        var extra = options.Parse(args);
        if (extra.Count != 1 || help)
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
    var help = false;
    var options = new FilterOptionSet
    {
        { "h|help", "print help", h => { if (h != null) help = true; } },
    };

    try
    {
        var extra = options.Parse(args);
        if (extra.Count != 1 || help)
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
            Write($"{compilerCall.ProjectFile} ({compilerCall.TargetFramework})");
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

int RunHelp()
{
    WriteLine("""
        compilerlog [command] [args]
        Commands
          create      Create a compilerlog file 
          print       Print summary of entries in the log
          help        Print help
        """);
    return ExitFailure;
}


