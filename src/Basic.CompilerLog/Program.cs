using Basic.CompilerLog;
using Basic.CompilerLog.Util;
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
        "analyzers" => RunAnalyzers(rest),
        "print" => RunPrint(rest),
        "help" => RunHelp(),
        _ => RunHelp()
    };
}
catch (LogException e)
{
    WriteLine(e.Message);
    return ExitFailure;
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

        var compilerLogFileName = $"{Path.GetFileNameWithoutExtension(binlogFilePath)}.complog";
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
    catch (Exception e) when (e is OptionException or LogException)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog create [OPTIONS] binlog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunAnalyzers(IEnumerable<string> args)
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
        using var reader = CompilerLogReader.Create(compilerLogStream, leaveOpen: true);
        var compilerCalls = reader.ReadCompilerCalls(options.FilterCompilerCalls);

        foreach (var compilerCall in compilerCalls)
        {
            WriteLine($"{compilerCall.ProjectFilePath} ({compilerCall.TargetFramework})");
            foreach (var tuple in reader.ReadAnalyzerFileInfo(compilerCall))
            {
                WriteLine($"\t{tuple.FilePath}");
            }
        }

        return ExitSuccess;
    }
    catch (Exception e) when (e is OptionException or LogException)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog analyzers [OPTIONS] build.complog");
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
    catch (Exception e) when (e is OptionException or LogException)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog print [OPTIONS] build.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunReferences(IEnumerable<string> args)
{
    var baseOutputPath = "";
    var options = new FilterOptionSet()
    {
        { "o|out", "path to output reference files", o => baseOutputPath = o },
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

        baseOutputPath = GetBaseOutputPath(baseOutputPath);
        WriteLine($"Copying references to {baseOutputPath}");
        Directory.CreateDirectory(baseOutputPath);

        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var refDirPath = GetOutputPath(baseOutputPath, compilerCalls, i, "refs");
            Directory.CreateDirectory(refDirPath);
            foreach (var tuple in reader.ReadReferenceFileInfo(compilerCall))
            {
                var filePath = Path.Combine(refDirPath, tuple.FileName);
                File.WriteAllBytes(filePath, tuple.ImageBytes);
            }

            var analyzerDirPath = GetOutputPath(baseOutputPath, compilerCalls, i, "analyzers");
            var groupMap = new Dictionary<string, string>(PathUtil.Comparer);
            foreach (var tuple in reader.ReadAnalyzerFileInfo(compilerCall))
            {
                var groupDir = GetGroupDirectoryPath();
                var filePath = Path.Combine(groupDir, Path.GetFileName(tuple.FilePath));
                File.WriteAllBytes(filePath, tuple.ImageBytes);

                string GetGroupDirectoryPath()
                {
                    var key = Path.GetDirectoryName(tuple.FilePath)!;
                    var first = false;
                    if (!groupMap.TryGetValue(key, out var groupName))
                    {
                        groupName = $"group{groupMap.Count}";
                        groupMap[key] = groupName;
                        first = true;
                    }

                    var groupDir = Path.Combine(analyzerDirPath, groupName);

                    if (first)
                    {
                        Directory.CreateDirectory(groupDir);
                        File.WriteAllText(Path.Combine(groupDir, "info.txt"), key);
                    }

                    return groupDir;
                }
            }
        }

        return ExitSuccess;
    }
    catch (Exception e) when (e is OptionException or LogException)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog rsp [OPTIONS] build.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunExport(IEnumerable<string> args)
{
    var baseOutputPath = "";
    var options = new FilterOptionSet()
    {
        { "o|out=", "path to output rsp files", o => baseOutputPath = o },
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

        baseOutputPath = GetBaseOutputPath(baseOutputPath);
        WriteLine($"Exporting to {baseOutputPath}");
        Directory.CreateDirectory(baseOutputPath);

        var sdkDirs = DotnetUtil.GetSdkDirectories();
        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var exportDir = GetOutputPath(baseOutputPath, compilerCalls, i, "export");
            exportUtil.ExportRsp(compilerCall, exportDir, sdkDirs);
        }

        return ExitSuccess;
    }
    catch (Exception e) when (e is OptionException or LogException)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog export [OPTIONS] build.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunResponseFile(IEnumerable<string> args)
{
    var singleLine = false;
    var baseOutputPath = "";
    var debugSdk = false;
    var options = new FilterOptionSet()
    {
        { "s|singleline", "keep response file as single line",  s => singleLine = s != null },
        { "debug-sdk", "generate sln files to debug rsp with the sdk", d => debugSdk = d != null },
        { "o|out=", "path to output rsp files", o => baseOutputPath = o },
    };

    try
    {
        var extra = options.Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        var compilerCalls = GetCompilerCalls(extra, options.FilterCompilerCalls);
        baseOutputPath = GetBaseOutputPath(baseOutputPath);
        WriteLine($"Generating response files in {baseOutputPath}");
        Directory.CreateDirectory(baseOutputPath);
        string? dotnetExePath = null;
        List<string>? sdkDirectories = null;

        if (debugSdk)
        {
            (dotnetExePath, sdkDirectories) = GetDotnetInfo();
        }

        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var rspDirPath = GetOutputPath(baseOutputPath, compilerCalls, i);
            Directory.CreateDirectory(rspDirPath);
            var rspFilePath = Path.Combine(rspDirPath, "build.rsp");
            using var writer = new StreamWriter(rspFilePath, append: false, Encoding.UTF8);
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

            // Emit the debug-7.0.200.sln style files if requested to make for easy 
            // debugging
            if (debugSdk)
            {
                var compilerDll = DotnetUtil.GetCompilerDll(compilerCall.IsCSharp);

                foreach (var sdkDir in sdkDirectories!)
                {
                    var compilerPath = Path.Combine(sdkDir, "Roslyn", "bincore", compilerDll);
                    var slnName = $"debug-{Path.GetFileName(sdkDir)}.sln";
                    var content = DotnetUtil.GetDebugSolutionFileContent(
                        Path.GetFileNameWithoutExtension(slnName),
                        dotnetExePath!,
                        compilerCall.ProjectDirectory,
                        $@"exec ""{compilerPath}"" ""{rspFilePath}""");
                    File.WriteAllText(Path.Combine(rspDirPath, slnName), content);
                }
            }
        }

        return ExitSuccess;
    }
    catch (Exception e) when (e is OptionException or LogException)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog rsp [OPTIONS] build.complog");
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
    catch (Exception e) when (e is OptionException or LogException)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog diagnostics [OPTIONS] compilerlog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunHelp()
{
    WriteLine("""
        complog [command] [args]
        Commands
          create        Create a compilerlog file 
          diagnostics   Print diagnostics for a compilation
          export        Export a complete project to disk
          rsp           Generate compiler response file for selected projects
          ref           Copy all references and analyzers to a single directory
          analyzers     Print analyzers used by a compilation
          print         Print summary of entries in the log
          help          Print help
        """);
    return ExitFailure;
}

List<CompilerCall> GetCompilerCalls(List<string> extra, Func<CompilerCall, bool>? predicate)
{
    var logFilePath = GetLogFilePath(extra);
    using var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    var ext = Path.GetExtension(logFilePath);
    switch (ext)
    {
        case ".binlog":
            return BinaryLogUtil.ReadCompilerCalls(stream, new(), predicate);
        case ".complog":
            return CompilerLogUtil.ReadCompilerCalls(stream, predicate);
        default:
            throw new Exception($"Unrecognized file extension: {ext}");
    }
}

Stream GetOrCreateCompilerLogStream(List<string> extra)
{
    var logFilePath = GetLogFilePath(extra);
    return CompilerLogUtil.GetOrCreateCompilerLogStream(logFilePath);
}

(string DotnetExePath, List<string> SdkDirectories) GetDotnetInfo()
{
    var dotnetExePath = DotnetUtil.GetDotnetExecutable();
    if (dotnetExePath is null)
    {
        throw new LogException("Cannot find dotnet");
    }

    var sdkDirectories = DotnetUtil.GetSdkDirectories();
    if (sdkDirectories.Count == 0)
    {
        throw new LogException("Cannot find sdk directories");
    }

    return (dotnetExePath, sdkDirectories);
}

string GetLogFilePath(List<string> extra)
{
    if (extra.Count > 1)
    {
        throw CreateLogException();
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
            .EnumerateFiles(baseDirectory, "*.complog")
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

        throw CreateLogException();
    }

    static LogException CreateLogException() => new("Need a path to a log file");
}

string GetBaseOutputPath(string? baseOutputPath)
{
    if (string.IsNullOrEmpty(baseOutputPath))
    {
        baseOutputPath = ".complog";
    }

    if (!Path.IsPathRooted(baseOutputPath))
    {
        baseOutputPath = Path.Combine(CurrentDirectory, baseOutputPath);
    }

    return baseOutputPath;
}

string GetOutputPath(string baseOutputPath, List<CompilerCall> compilerCalls, int index, string? directoryName = null)
{
    var projectName = GetProjectUniqueName(compilerCalls, index);
    return string.IsNullOrEmpty(directoryName)
        ? Path.Combine(baseOutputPath, projectName)
        : Path.Combine(baseOutputPath, projectName, directoryName);
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

internal sealed class LogException : Exception
{
    internal LogException(string message) 
        : base(message)
    {
    }
}
