using Basic.CompilerLog;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Mono.Options;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using static Constants;
using static System.Console;

var (command, rest) = args.Length == 0
    ? ("help", Enumerable.Empty<string>())
    : (args[0], args.Skip(1));
// a CancellationToken that is canceled when the user hits Ctrl+C.
var cts = new CancellationTokenSource();

CancelKeyPress += (s, e) =>
{
    WriteLine("Canceling...");
    cts.Cancel();
    e.Cancel = true;
};

try
{
    return command.ToLower() switch
    {
        "create" => RunCreate(rest),
        "diagnostics" => RunDiagnostics(rest),
        "export" => RunExport(rest),
        "ref" => RunReferences(rest),
        "rsp" => RunResponseFile(rest),
        "emit" => RunEmit(rest, cts.Token),
        "analyzers" => RunAnalyzers(rest),
        "print" => RunPrint(rest),
        "help" => RunHelp(rest),
        _ => RunHelp(null)
    };
}
catch (Exception e)
{
    RunHelp(null);
    WriteLine("Unexpected error");
    WriteLine(e.Message);
    return ExitFailure;
}

int RunCreate(IEnumerable<string> args)
{
    string? complogFilePath = null;
    var options = new FilterOptionSet()
    {
        { "o|out=", "path to output reference files", o => complogFilePath = o },
    };

    try
    {
        var extra = options.Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return ExitFailure;
        }

        // todo use the standard get or build code
        string binlogFilePath = GetLogFilePath(extra);
        if (PathUtil.Comparer.Equals(".complog", Path.GetExtension(binlogFilePath)))
        {
            WriteLine($"Already a .complog file: {binlogFilePath}");
            return ExitFailure;
        }

        if (complogFilePath is null)
        {
            complogFilePath = Path.ChangeExtension(binlogFilePath, ".complog");
        }

        complogFilePath = GetResolvedPath(CurrentDirectory, complogFilePath);
        var diagnosticList = CompilerLogUtil.ConvertBinaryLog(
            binlogFilePath,
            complogFilePath,
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
        WriteLine("complog create [OPTIONS] msbuild.binlog");
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
        using var reader = GetCompilerLogReader(compilerLogStream, leaveOpen: true);
        var compilerCalls = reader.ReadAllCompilerCalls(options.FilterCompilerCalls);

        foreach (var compilerCall in compilerCalls)
        {
            WriteLine(compilerCall.GetDiagnosticName());
            foreach (var tuple in reader.ReadAnalyzerFileInfo(compilerCall))
            {
                WriteLine($"\t{tuple.FilePath}");
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
        WriteLine("complog analyzers [OPTIONS] msbuild.complog");
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
        var compilerCalls = CompilerLogUtil.ReadAllCompilerCalls(
            compilerLogStream,
            options.FilterCompilerCalls);

        foreach (var compilerCall in compilerCalls)
        {
            WriteLine(compilerCall.GetDiagnosticName());
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
        WriteLine("complog print [OPTIONS] msbuild.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunReferences(IEnumerable<string> args)
{
    var baseOutputPath = "";
    var options = new FilterOptionSet()
    {
        { "o|out=", "path to output reference files", o => baseOutputPath = o },
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
        using var reader = GetCompilerLogReader(compilerLogStream, leaveOpen: true);
        var compilerCalls = reader.ReadAllCompilerCalls(options.FilterCompilerCalls);

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
    catch (OptionException e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog rsp [OPTIONS] msbuild.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunExport(IEnumerable<string> args)
{
    var baseOutputPath = "";
    var excludeAnalyzers = false;
    var options = new FilterOptionSet()
    {
        { "o|out=", "path to export build content", o => baseOutputPath = o },
        { "e|exclude-analyzers", "emit the compilation without analyzers / generators", e => excludeAnalyzers = e is not null },
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
        using var reader = GetCompilerLogReader(compilerLogStream, leaveOpen: true);
        var compilerCalls = reader.ReadAllCompilerCalls(options.FilterCompilerCalls);
        var exportUtil = new ExportUtil(reader, includeAnalyzers: !excludeAnalyzers);

        baseOutputPath = GetBaseOutputPath(baseOutputPath);
        WriteLine($"Exporting to {baseOutputPath}");
        Directory.CreateDirectory(baseOutputPath);

        var sdkDirs = DotnetUtil.GetSdkDirectories();
        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var exportDir = GetOutputPath(baseOutputPath, compilerCalls, i, "export");
            exportUtil.Export(compilerCall, exportDir, sdkDirs);
        }

        return ExitSuccess;
    }
    catch (Exception e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog export [OPTIONS] msbuild.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunResponseFile(IEnumerable<string> args)
{
    var singleLine = false;
    var baseOutputPath = "";
    var options = new FilterOptionSet()
    {
        { "s|singleline", "keep response file as single line",  s => singleLine = s != null },
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

        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var rspDirPath = GetOutputPath(baseOutputPath, compilerCalls, i);
            Directory.CreateDirectory(rspDirPath);
            var rspFilePath = Path.Combine(rspDirPath, "build.rsp");
            using var writer = new StreamWriter(rspFilePath, append: false, Encoding.UTF8);
            ExportUtil.ExportRsp(compilerCall, writer, singleLine);
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
        WriteLine("complog rsp [OPTIONS] msbuild.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunEmit(IEnumerable<string> args, CancellationToken cancellationToken)
{
    var baseOutputPath = "";
    var options = new FilterOptionSet(includeNoneHost: true)
    {
        { "o|out=", "path to output binaries to", o => baseOutputPath = o },
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
        using var reader = GetCompilerLogReader(compilerLogStream, leaveOpen: true);
        var compilerCalls = reader.ReadAllCompilerCalls(options.FilterCompilerCalls);
        var allSucceeded = true;

        baseOutputPath = GetBaseOutputPath(baseOutputPath);
        WriteLine($"Generating binary files to {baseOutputPath}");
        Directory.CreateDirectory(baseOutputPath);

        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];
            var emitDirPath = GetOutputPath(baseOutputPath, compilerCalls, i, "emit");
            Directory.CreateDirectory(emitDirPath);

            Write($"{compilerCall.GetDiagnosticName()} ... ");
            var compilationData = reader.ReadCompilationData(compilerCall);
            var result = compilationData.EmitToDisk(emitDirPath, cancellationToken);
            if (result.Success)
            {
                WriteLine("done");
            }
            else
            {
                allSucceeded = false;
                WriteLine("FAILED");
                foreach (var diagnostic in result.Diagnostics)
                {
                    WriteLine(diagnostic.GetMessage());
                }
            }
        }

        return allSucceeded ? ExitSuccess : ExitFailure;
    }
    catch (OptionException e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog rsp [OPTIONS] msbuild.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunDiagnostics(IEnumerable<string> args)
{
    var severity = DiagnosticSeverity.Warning;
    var options = new FilterOptionSet(includeNoneHost: true)
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
        using var reader = GetCompilerLogReader(compilerLogStream, leaveOpen: true);
        var compilationDatas = reader.ReadAllCompilationData(options.FilterCompilerCalls);

        foreach (var compilationData in compilationDatas)
        {
            var compilerCall = compilationData.CompilerCall;
            WriteLine(compilerCall.GetDiagnosticName());
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
        WriteLine("complog diagnostics [OPTIONS] msbuild.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunHelp(IEnumerable<string>? args)
{
    var verbose = false;
    if (args is not null)
    {
        var options = new OptionSet()
        {
            { "v|verbose", "verbose output", o => { if (o is not null) verbose = true; } },
        };
        options.Parse(args);
    }

    WriteLine("""
        complog [command] [args]
        Commands
          create        Create a compiler log file 
          diagnostics   Print diagnostics for a compilation
          export        Export compilation contents, rsp and build files to disk
          rsp           Generate compiler response file projects on this machine
          ref           Copy all references and analyzers to a single directory
          emit          Emit all binaries from the log
          analyzers     Print analyzers used by a compilation
          print         Print summary of entries in the log
          help          Print help
        """);

    if (verbose)
    {
        WriteLine("""
        Commands can be passed a .complog, .binlog, .sln or .csproj file. In the case of build 
        files a 'dotnet build' will be used to create a binlog file. Extra build args can be 
        passed after --. 
        
        For example: complog create console.csproj -- -p:Configuration=Release

        """);
    }

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
            return BinaryLogUtil.ReadAllCompilerCalls(stream, new(), predicate);
        case ".complog":
            return CompilerLogUtil.ReadAllCompilerCalls(stream, predicate);
        default:
            throw new Exception($"Unrecognized file extension: {ext}");
    }
}

CompilerLogReader GetCompilerLogReader(Stream compilerLogStream, bool leaveOpen)
{
    var reader = CompilerLogReader.Create(compilerLogStream, leaveOpen);
    if (reader.MetadataVersion > CompilerLogReader.LatestMetadataVersion)
    {
        WriteLine($"Compiler log version newer than toolset: {reader.MetadataVersion}");
        WriteLine($"Consider upgrading to latest compiler log toolset");
        WriteLine("dotnet tool update --global Basic.CompilerLog");
    }

    return reader;
}

Stream GetOrCreateCompilerLogStream(List<string> extra)
{
    var logFilePath = GetLogFilePath(extra);
    return CompilerLogUtil.GetOrCreateCompilerLogStream(logFilePath);
}

/// <summary>
/// Returns a path to a .complog or .binlog to be used for processing
/// </summary>
string GetLogFilePath(List<string> extra)
{
    string? logFilePath;
    IEnumerable<string> args = Array.Empty<string>();
    string baseDirectory = CurrentDirectory;
    if (extra.Count == 0)
    {
        logFilePath = FindLogFilePath(baseDirectory);
    }
    else
    {
        logFilePath = extra[0];
        args = extra.Skip(1);
        if (string.IsNullOrEmpty(Path.GetExtension(logFilePath)))
        {
            baseDirectory = logFilePath;
            logFilePath = FindLogFilePath(baseDirectory);
        }
    }

    if (logFilePath is null)
    {
        throw CreateOptionException();
    }

    switch (Path.GetExtension(logFilePath))
    {
        case ".complog":
        case ".binlog":
            if (args.Any())
            {
                throw new OptionException($"Extra arguments: {string.Join(' ', args.Skip(1))}", "log");
            }

            return GetResolvedPath(CurrentDirectory, logFilePath);
        case ".sln":
        case ".csproj":
        case ".vbproj":
            return GetLogFilePathAfterBuild(baseDirectory, logFilePath, args);
        default:
            throw new OptionException($"Not a valid log file {logFilePath}", "log");
    }

    static string? FindLogFilePath(string baseDirectory) =>
        FindFirstFileWithPattern(baseDirectory, "*.complog", "*.binlog", "*.sln", "*.csproj", ".vbproj");

    static string GetLogFilePathAfterBuild(string baseDirectory, string? buildFileName, IEnumerable<string> buildArgs)
    {
        var path = buildFileName is not null
            ? GetResolvedPath(baseDirectory, buildFileName)
            : FindFirstFileWithPattern(baseDirectory, "*.sln", "*.csproj", ".vbproj");
        if (path is null)
        {
            throw CreateOptionException();
        }

        var tag = buildArgs.Any() ? "" : "-t:Rebuild";
        var args = $"build {path} -bl:complog.binlog {tag} {string.Join(' ', buildArgs)}";
        WriteLine($"Building {path}");
        WriteLine($"dotnet {args}");
        var result = ProcessUtil.Run("dotnet", args, baseDirectory);
        WriteLine(result.StandardOut);
        WriteLine(result.StandardError);
        if (!result.Succeeded)
        {
            throw new Exception("Build failed");
        }

        return Path.Combine(baseDirectory, "complog.binlog");
    }

    static OptionException CreateOptionException() => new("Need a file to analyze", "log");
}

static string? FindFirstFileWithPattern(string baseDirectory, params string[] patterns)
{
    foreach (var pattern in patterns)
    {
        var path = Directory
            .EnumerateFiles(baseDirectory, pattern)
            .OrderBy(x => Path.GetFileName(x), PathUtil.Comparer)
            .FirstOrDefault();
        if (path is not null)
        {
            return path;
        }
    }

    return null;
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

static string GetResolvedPath(string baseDirectory, string path)
{
    if (Path.IsPathRooted(path))
    {
        return path;
    }

    return Path.Combine(baseDirectory, path);
}
