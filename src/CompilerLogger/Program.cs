using Basic.CompilerLogger;
using Mono.Options;
using static CompilerLogger.Constants;
using static System.Console;

var (command, rest) = args.Length == 0
    ? ("help", Enumerable.Empty<string>())
    : (args[0], args.Skip(1));

try
{
    return command switch
    {
        "create" => RunCreate(rest),
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
    var includeSatelliteAssemblies = false;
    var targetFrameworks = new List<string>();
    var help = false;
    var options = new OptionSet
    {
        { "s|satellite", "include satellite assemwblies", s => { if (s != null) includeSatelliteAssemblies = true; } },
        { "targetframework", "include only compilations for the target framework (allows multiple)", tf => targetFrameworks.Add(tf) },
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
            c =>
            {
                if (!includeSatelliteAssemblies && c.CompilationKind == CompilationKind.Sattelite)
                {
                    return false;
                }

                if (targetFrameworks.Count > 0 && !targetFrameworks.Contains(c.TargetFramework, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            });
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

int RunHelp()
{
    WriteLine("""
        compilerlog [command] [args]
        Commands
          create      Create a compilerlog file 
          help        Print help
        """);
    return ExitFailure;
}


