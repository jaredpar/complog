using Basic.CompilerLog;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Mono.Options;
using StructuredLogViewer;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using static Constants;

var (command, rest) = args.Length == 0
    ? ("help", Enumerable.Empty<string>())
    : (args[0], args.Skip(1));

try
{
    return command.ToLower() switch
    {
        "create" => RunCreate(rest),
        "replay" => RunReplay(rest),
        "export" => RunExport(rest),
        "ref" => RunReferences(rest),
        "rsp" => RunResponseFile(rest),
        "analyzers" => RunAnalyzers(rest),
        "print" => RunPrint(rest),
        "help" => RunHelp(rest),

        // Older option names
        "diagnostics" => RunReplay(rest),
        "emit" => RunReplay(rest),
        _ => RunBadCommand(command)
    };
}
catch (Exception e)
{
    WriteLine("Unexpected error");
    WriteLine(e.Message);
    RunHelp(null);
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

        string binlogFilePath = GetLogFilePath(extra, includeCompilerLogs: false);
        if (PathUtil.Comparer.Equals(".complog", Path.GetExtension(binlogFilePath)))
        {
            WriteLine($"Already a .complog file: {binlogFilePath}");
            return ExitFailure;
        }

        if (complogFilePath is null)
        {
            complogFilePath = Path.ChangeExtension(Path.GetFileName(binlogFilePath), ".complog");
        }

        complogFilePath = GetResolvedPath(CurrentDirectory, complogFilePath);
        var convertResult = CompilerLogUtil.TryConvertBinaryLog(binlogFilePath, complogFilePath, options.FilterCompilerCalls);
        foreach (var diagnostic in convertResult.Diagnostics)
        {
           WriteLine(diagnostic);
        }

        if (options.ProjectNames.Count > 0)
        {
            foreach (var compilerCall in convertResult.CompilerCalls)
            {
                WriteLine(compilerCall.GetDiagnosticName());
            }
        }

        if (convertResult.CompilerCalls.Count == 0)
        {
            WriteLine($"No compilations added");
            return ExitFailure;
        }

        return convertResult.Succeeded ? ExitSuccess : ExitFailure;
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
            return ExitSuccess;
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
            return ExitSuccess;
        }

        using var compilerLogStream = GetOrCreateCompilerLogStream(extra);
        using var reader = GetCompilerLogReader(compilerLogStream, leaveOpen: true);
        var compilerCalls = reader.ReadAllCompilerCalls(options.FilterCompilerCalls);

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
            return ExitSuccess;
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
        WriteLine("complog ref [OPTIONS] msbuild.complog");
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
            return ExitSuccess;
        }

        using var compilerLogStream = GetOrCreateCompilerLogStream(extra);
        using var reader = GetCompilerLogReader(compilerLogStream, leaveOpen: true);
        var compilerCalls = reader.ReadAllCompilerCalls(options.FilterCompilerCalls);
        var exportUtil = new ExportUtil(reader, includeAnalyzers: !excludeAnalyzers);

        baseOutputPath = GetBaseOutputPath(baseOutputPath);
        WriteLine($"Exporting to {baseOutputPath}");
        Directory.CreateDirectory(baseOutputPath);

        var sdkDirs = SdkUtil.GetSdkDirectories();
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
            return ExitSuccess;
        }

        var (disposable, compilerCalls) = GetCompilerCalls(extra, options.FilterCompilerCalls);
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

        disposable.Dispose();
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

    (IDisposable disposable, List<CompilerCall>) GetCompilerCalls(List<string> extra, Func<CompilerCall, bool>? predicate)
    {
        var logFilePath = GetLogFilePath(extra);
        var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var ext = Path.GetExtension(logFilePath);
        if (ext is ".binlog")
        {
            return (stream, BinaryLogUtil.ReadAllCompilerCalls(stream, new(), predicate));
        }
        else
        {
            Debug.Assert(ext is ".complog");
            var reader = GetCompilerLogReader(stream, leaveOpen: false);
            return (reader, reader.ReadAllCompilerCalls(predicate));
        }
    }
}

int RunReplay(IEnumerable<string> args)
{
    var baseOutputPath = "";
    var severity = DiagnosticSeverity.Warning;
    var export = false;
    var emit = false;
    var analyzers = false;
    var options = new FilterOptionSet(includeNoneHost: true)
    {
        { "severity=", "minimum severity to display (default Warning)", (DiagnosticSeverity s) => severity = s },
        { "export", "export failed compilation", e => export = e is not null },
        { "emit", "emit the compilation(s) to disk", e => emit = e is not null },
        { "analyzers", "use actual analyzers / generators (default no)", a => analyzers = a is not null },
        { "o|out=", "path to export to ", b => baseOutputPath = b },
    };

    try
    {
        var extra = options.Parse(args);
        if (options.Help)
        {
            PrintUsage();
            return ExitSuccess;
        }

        if (!string.IsNullOrEmpty(baseOutputPath) && !(export || emit))
        {
            WriteLine("Error: Specified a path to export to but did not specify -export or -emit");
            return ExitFailure;
        }

        baseOutputPath = GetBaseOutputPath(baseOutputPath);
        if (!string.IsNullOrEmpty(baseOutputPath))
        {
            WriteLine($"Outputting to {baseOutputPath}");
        }

        var analyzerHostOptions = analyzers ? BasicAnalyzerHostOptions.Default : BasicAnalyzerHostOptions.None;
        using var compilerLogStream = GetOrCreateCompilerLogStream(extra);
        using var reader = GetCompilerLogReader(compilerLogStream, leaveOpen: true, analyzerHostOptions);
        var compilerCalls = reader.ReadAllCompilerCalls(options.FilterCompilerCalls);
        var exportUtil = new ExportUtil(reader, includeAnalyzers: analyzerHostOptions.Kind != BasicAnalyzerKind.None);
        var sdkDirs = SdkUtil.GetSdkDirectories();
        var success = true;

        for (int i = 0; i < compilerCalls.Count; i++)
        {
            var compilerCall = compilerCalls[i];

            Write($"{compilerCall.GetDiagnosticName()} ...");

            var compilationData = reader.ReadCompilationData(compilerCall);
            var compilation = compilationData.GetCompilationAfterGenerators();

            IEmitResult emitResult;
            if (emit)
            {
                var path = GetOutputPath(baseOutputPath, compilerCalls, i, "emit");
                Directory.CreateDirectory(path);
                emitResult = compilationData.EmitToDisk(path);
            }
            else
            {
                emitResult = compilationData.EmitToMemory();
            }

            WriteLine(emitResult.Success ? "Success" : "Error");
            foreach (var diagnostic in emitResult.Diagnostics)
            {
                if (diagnostic.Severity >= severity)
                {
                    Write("    ");
                    WriteLine(diagnostic.GetMessage());
                }
            }

            if (!emitResult.Success && export)
            {
                var exportPath = GetOutputPath(baseOutputPath, compilerCalls, i, "export");
                Directory.CreateDirectory(exportPath);
                WriteLine($"Exporting to {exportPath}");
                exportUtil.Export(compilationData.CompilerCall, exportPath, sdkDirs);
                success = false;
            }
        }

        return success ? ExitSuccess : ExitFailure;
    }
    catch (OptionException e)
    {
        WriteLine(e.Message);
        PrintUsage();
        return ExitFailure;
    }

    void PrintUsage()
    {
        WriteLine("complog replay [OPTIONS] msbuild.complog");
        options.WriteOptionDescriptions(Out);
    }
}

int RunBadCommand(string command)
{
    WriteLine(@$"""{command}"" is not a valid command");
    _ = RunHelp(null);
    return ExitFailure;
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
          replay        Replay compilations from the log
          export        Export compilation contents, rsp and build files to disk
          rsp           Generate compiler response file projects on this machine
          ref           Copy all references and analyzers to a single directory
          diagnostics   Print diagnostics for a compilation
          analyzers     Print analyzers / generators used by a compilation
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

    return ExitSuccess;
}

CompilerLogReader GetCompilerLogReader(Stream compilerLogStream, bool leaveOpen, BasicAnalyzerHostOptions? options = null)
{
    var reader = CompilerLogReader.Create(compilerLogStream, leaveOpen, options);
    if (reader.MetadataVersion > CompilerLogReader.LatestMetadataVersion)
    {
        WriteLine($"Compiler log version newer than toolset: {reader.MetadataVersion}");
        WriteLine($"Consider upgrading to latest compiler log toolset");
        WriteLine("dotnet tool update --global complog");
    }

    if (reader.IsWindowsLog != RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        WriteLine($"Compiler log generated on different operating system");
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
string GetLogFilePath(List<string> extra, bool includeCompilerLogs = true)
{
    string? logFilePath;
    IEnumerable<string> args = Array.Empty<string>();
    string baseDirectory = CurrentDirectory;
    var printFile = false;
    if (extra.Count == 0)
    {
        logFilePath = FindLogFilePath(baseDirectory, includeCompilerLogs);
        printFile = true;
    }
    else
    {
        logFilePath = extra[0];
        args = extra.Skip(1);
        if (string.IsNullOrEmpty(Path.GetExtension(logFilePath)) && Directory.Exists(logFilePath))
        {
            baseDirectory = logFilePath;
            logFilePath = FindLogFilePath(baseDirectory, includeCompilerLogs);
            printFile = true;
        }
    }

    if (logFilePath is null)
    {
        throw CreateOptionException();
    }

    // If the file wasn't explicitly specified let the user know what file we are using
    if (printFile)
    {
        WriteLine($"Using {logFilePath}");
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

    static string? FindLogFilePath(string baseDirectory, bool includeCompilerLogs = true ) =>
        includeCompilerLogs
            ? FindFirstFileWithPattern(baseDirectory, "*.complog", "*.binlog", "*.sln", "*.csproj", ".vbproj")
            : FindFirstFileWithPattern(baseDirectory, "*.binlog", "*.sln", "*.csproj", ".vbproj");

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
        var args = $"build {path} -bl:build.binlog -nr:false {tag} {string.Join(' ', buildArgs)}";
        WriteLine($"Building {path}");
        WriteLine($"dotnet {args}");
        var result = DotnetUtil.Command(args, baseDirectory);
        WriteLine(result.StandardOut);
        WriteLine(result.StandardError);
        if (!result.Succeeded)
        {
            WriteLine("Build Failed!");
        }

        return Path.Combine(baseDirectory, "build.binlog");
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

static void Write(string str) => Constants.Out.Write(str);
static void WriteLine(string line) => Constants.Out.WriteLine(line);
