using Basic.CompilerLog;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Mono.Options;
using StructuredLogViewer;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using static Basic.CompilerLog.App.Constants;

namespace Basic.CompilerLog.App;

public sealed class CompilerLogApp(
    string? workingDirectory = null,
    string? appDataDirectory = null,
    TextWriter? outputWriter = null,
    Action<ICompilerCallReader>? onCompilerCallReader = null)
{
    public string WorkingDirectory { get; } = workingDirectory ?? Environment.CurrentDirectory;
    public string AppDataDirectory { get; } = appDataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Basic.CompilerLog",
        Guid.NewGuid().ToString());
    public TextWriter OutputWriter { get; } = outputWriter ?? Console.Out;
    private Action<ICompilerCallReader> OnCompilerCallReader { get; } = onCompilerCallReader ?? (_ => { });
    private bool IsCustomCompiler { get; init; }

    public int Run(string[] args)
    {
        var (command, rest) = args.Length == 0
            ? ("help", Enumerable.Empty<string>())
            : (args[0], args.Skip(1));

        try
        {
            return command.ToLowerInvariant() switch
            {
                "create" => RunCreate(rest),
                "replay" => RunReplay(rest),
                "export" => RunExport(rest),
                "ref" => RunReferences(rest),
                "rsp" => RunResponseFile(rest),
                "analyzers" => RunAnalyzers(rest),
                "generated" => RunGenerated(rest),
                "hash" => RunHash(rest),
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
            WriteLine($"Unexpected error: {e}");
            RunHelp(null);
            return ExitFailure;
        }
        finally
        {
            if (Directory.Exists(AppDataDirectory))
            {
                Directory.Delete(AppDataDirectory, recursive: true);
            }
        }
    }

    internal int RunCreate(IEnumerable<string> args)
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

            complogFilePath = GetResolvedPath(WorkingDirectory, complogFilePath);
            var convertResult = CompilerLogUtil.TryConvertBinaryLog(binlogFilePath, complogFilePath, predicate: null);
            foreach (var diagnostic in convertResult.Diagnostics)
            {
                WriteLine(diagnostic);
            }

            if (options.ProjectNames.Count > 0)
            {
                foreach (var compilerCall in convertResult.CompilerCalls.Where(options.FilterCompilerCalls))
                {
                    WriteLine(compilerCall.GetDiagnosticName());
                }
            }

            if (convertResult.CompilerCalls.Count == 0)
            {
                WriteLine($"No compilations added");
                return ExitFailure;
            }

            WriteLine($"Wrote {complogFilePath}");

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
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunAnalyzers(IEnumerable<string> args)
    {
        var includeTypes = false;
        var includePath = false;
        var options = new FilterOptionSet()
        {
            { "t|types", "include type names", o => includeTypes = o is not null },
            { "path", "include analyzer file path", o => includePath = o is not null },
        };

        try
        {
            var extra = options.Parse(args);
            if (options.Help)
            {
                PrintUsage();
                return ExitSuccess;
            }

            using var reader = GetCompilerCallReader(extra, BasicAnalyzerKind.None);
            var compilerCalls = ReadAllCompilerCalls(reader, options.FilterCompilerCalls);
            foreach (var compilerCall in compilerCalls)
            {
                WriteLine(compilerCall.GetDiagnosticName());
                foreach (var data in reader.ReadAllAnalyzerData(compilerCall))
                {
                    var name = includePath ? data.FilePath : data.FileName;
                    WriteLine($"\t{name}");

                    if (includeTypes)
                    {
                        var (analyzers, generators) = reader.ReadAnalyzerFullTypeNames(data, compilerCall.IsCSharp);
                        WriteLine($"\tAnalyzers:");
                        foreach (var analyzer in analyzers)
                        {
                            WriteLine($"\t\t{analyzer}");
                        }

                        WriteLine($"\tGenerators:");
                        foreach (var generator in generators)
                        {
                            WriteLine($"\t\t{generator}");
                        }
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
            WriteLine("complog analyzers [OPTIONS] msbuild.complog");
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunPrint(IEnumerable<string> args)
    {
        var compilers = false;
        var analyzers = false;
        var options = new FilterOptionSet()
        {
            { "c|compilers", "include compiler summary", c => compilers = c is not null },
            { "a|analyzers", "include analyzer summary", a => analyzers = a is not null },
        };

        try
        {
            var extra = options.Parse(args);
            if (options.Help)
            {
                PrintUsage();
                return ExitSuccess;
            }

            using var reader = GetCompilerCallReader(extra, BasicAnalyzerKind.None);
            var compilerCalls = ReadAllCompilerCalls(reader, options.FilterCompilerCalls);

            WriteLine("Projects");
            foreach (var compilerCall in compilerCalls)
            {
                WriteLine($"\t{compilerCall.GetDiagnosticName()}");

                if (analyzers)
                {
                    WriteLine("\tAnalyzers");

                    foreach (var analyzer in reader.ReadAllAnalyzerData(compilerCall))
                    {
                        WriteLine($"\t\tName: {analyzer.AssemblyIdentityData.AssemblyName ?? "<null>"}");
                        WriteLine($"\t\tInformational Version: {analyzer.AssemblyIdentityData.AssemblyInformationalVersion ?? "<null>"}");
                        WriteLine($"\t\tFile Path: {analyzer.FilePath}");
                    }
                }
            }

            if (compilers)
            {
                WriteLine("Compilers");
                foreach (var tuple in reader.ReadAllCompilerAssemblies())
                {
                    WriteLine($"\tFile Path: {tuple.FilePath}");
                    WriteLine($"\tAssembly Name: {tuple.AssemblyName}");
                    WriteLine($"\tCommit Hash: {tuple.CommitHash}");
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
            WriteLine("complog print [OPTIONS] msbuild.complog");
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunReferences(IEnumerable<string> args)
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

            using var reader = GetCompilerCallReader(extra, BasicAnalyzerKind.None);
            var compilerCalls = ReadAllCompilerCalls(reader, options.FilterCompilerCalls);
            var compilerCallNames = GetCompilerCallNames(compilerCalls);

            baseOutputPath = GetBaseOutputPath(baseOutputPath, "refs");
            WriteLine($"Copying references to {baseOutputPath}");
            Directory.CreateDirectory(baseOutputPath);

            for (int i = 0; i < compilerCalls.Count; i++)
            {
                var compilerCall = compilerCalls[i];
                var refDirPath = Path.Combine(baseOutputPath, compilerCallNames[i], "refs");
                Directory.CreateDirectory(refDirPath);
                foreach (var data in reader.ReadAllReferenceData(compilerCall))
                {
                    var filePath = Path.Combine(refDirPath, data.FileName);
                    WriteTo(data.AssemblyData, filePath);
                }

                var analyzerDirPath = Path.Combine(baseOutputPath, compilerCallNames[i], "analyzers");
                var groupMap = new Dictionary<string, string>(PathUtil.Comparer);
                foreach (var data in reader.ReadAllAnalyzerData(compilerCall))
                {
                    var groupDir = GetGroupDirectoryPath();
                    var filePath = Path.Combine(groupDir, data.FileName);
                    WriteTo(data.AssemblyData, filePath);

                    string GetGroupDirectoryPath()
                    {
                        var key = Path.GetDirectoryName(data.FilePath)!;
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

                void WriteTo(AssemblyData referenceData, string filePath)
                {
                    using var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                    reader.CopyAssemblyBytes(referenceData, stream);
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
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunExport(IEnumerable<string> args)
    {
        var baseOutputPath = "";
        var excludeAnalyzers = false;
        var options = new FilterOptionSet()
        {
            { "o|out=", "path to export build content", o => baseOutputPath = o },
            { "n|no-analyzers", "do not include analyzers in rsp", i => excludeAnalyzers = i is not null },
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
            using var reader = GetCompilerLogReader(compilerLogStream, leaveOpen: true, BasicAnalyzerKind.None);
            var compilerCalls = ReadAllCompilerCalls(reader, options.FilterCompilerCalls);
            var compilerCallNames = GetCompilerCallNames(compilerCalls);
            var exportUtil = new ExportUtil(reader, excludeAnalyzers);

            baseOutputPath = GetBaseOutputPath(baseOutputPath, "export");
            WriteLine($"Exporting to {baseOutputPath}");
            Directory.CreateDirectory(baseOutputPath);

            var sdkDirs = SdkUtil.GetSdkDirectories();
            for (int i = 0; i < compilerCalls.Count; i++)
            {
                var compilerCall = compilerCalls[i];
                var exportDir = Path.Combine(baseOutputPath, compilerCallNames[i]);
                exportUtil.Export(compilerCall, exportDir, sdkDirs);
            }

            return ExitSuccess;
        }
        catch (Exception e)
        {
            WriteLine(e.GetFailureString());
            PrintUsage();
            return ExitFailure;
        }

        void PrintUsage()
        {
            WriteLine("complog export [OPTIONS] msbuild.complog");
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunResponseFile(IEnumerable<string> args)
    {
        var singleLine = false;
        var inline = false;
        var baseOutputPath = "";
        var options = new FilterOptionSet()
        {
            { "s|singleline", "keep response file as single line",  s => singleLine = s != null },
            { "i|inline", "put response files next to the project file", i => inline = i != null },
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

            if (inline && !string.IsNullOrEmpty(baseOutputPath))
            {
                WriteLine("Cannot specify both --inline and --out");
                return ExitFailure;
            }

            using var reader = GetCompilerCallReader(extra, BasicAnalyzerHost.DefaultKind);
            if (inline)
            {
                WriteLine("Generating response files inline");
            }
            else
            {
                baseOutputPath = GetBaseOutputPath(baseOutputPath, "rsp");
                WriteLine($"Generating response files in {baseOutputPath}");
                Directory.CreateDirectory(baseOutputPath);
            }

            var compilerCalls = ReadAllCompilerCalls(reader, options.FilterCompilerCalls);
            var compilerCallNames = GetCompilerCallNames(compilerCalls);
            for (int i = 0; i < compilerCalls.Count; i++)
            {
                var compilerCall = compilerCalls[i];
                var rspDirPath = inline
                    ? compilerCall.ProjectDirectory
                    : Path.Combine(baseOutputPath, compilerCallNames[i]);
                Directory.CreateDirectory(rspDirPath);
                var rspFilePath = Path.Combine(rspDirPath, GetRspFileName());
                using var writer = new StreamWriter(rspFilePath, append: false, Encoding.UTF8);

                if (!singleLine)
                {
                    bool hasNoConfigOption = reader.ReadArguments(compilerCall).Contains("/noconfig", StringComparer.OrdinalIgnoreCase);
                    writer.WriteLine($"# cd {compilerCall.ProjectDirectory}");
                    writer.WriteLine($"# csc {(hasNoConfigOption ? "/noconfig " : "")}@\"{rspFilePath}\"");
                }

                ExportUtil.ExportRsp(reader.ReadArguments(compilerCall), writer, singleLine);

                string GetRspFileName()
                {
                    if (inline)
                    {
                        return IsSingleTarget(compilerCall, compilerCalls)
                            ? "build.rsp"
                            : $"build-{compilerCall.TargetFramework}.rsp";
                    }

                    return "build.rsp";
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
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunReplay(IEnumerable<string> args)
    {
        string? baseOutputPath = null;
        var severity = DiagnosticSeverity.Warning;
        var options = new FilterOptionSet(analyzers: true)
        {
            { "severity=", "minimum severity to display (default Warning)", (DiagnosticSeverity s) => severity = s },
            { "o|out=", "path to emit to ", void (string b) => baseOutputPath = b },
        };

        try
        {
            var extra = options.Parse(args);
            if (options.Help)
            {
                PrintUsage();
                return ExitSuccess;
            }

            if (options.CustomCompilerFilePath is not null && !IsCustomCompiler)
            {
                return RunInContext(["replay", .. args], options.CustomCompilerFilePath);
            }

            if (baseOutputPath is not null)
            {
                baseOutputPath = GetBaseOutputPath(baseOutputPath);
                WriteLine($"Outputting to {baseOutputPath}");
            }

            using var reader = GetCompilerCallReader(extra, options.BasicAnalyzerKind, checkVersion: true, new(cacheAnalyzers: true));
            var compilerCalls = ReadAllCompilerCalls(reader, options.FilterCompilerCalls);
            var compilerCallNames = GetCompilerCallNames(compilerCalls);
            var allSucceeded = true;

            for (int i = 0; i < compilerCalls.Count; i++)
            {
                var compilerCall = compilerCalls[i];

                Write($"{compilerCall.GetDiagnosticName()} ...");

                var compilationData = reader.ReadCompilationData(compilerCall);
                var compilation = compilationData.GetCompilationAfterGenerators(out var analyzerDiagnostics);
                var succeeded = !analyzerDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

                IEmitResult emitResult;
                if (baseOutputPath is not null)
                {
                    var path = Path.Combine(baseOutputPath, compilerCallNames[i]);
                    Directory.CreateDirectory(path);
                    emitResult = compilationData.EmitToDisk(path);
                }
                else
                {
                    emitResult = compilationData.EmitToMemory();
                }

                if (!emitResult.Success)
                {
                    succeeded = false;
                }

                WriteLine(succeeded ? "Success" : "Error");

                foreach (var diagnostic in analyzerDiagnostics.Concat(emitResult.Diagnostics))
                {
                    if (diagnostic.Severity >= severity)
                    {
                        WriteLine($"    {diagnostic.Id}: {diagnostic.GetMessage()}");
                    }
                }

                if (!succeeded)
                {
                    allSucceeded = false;
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
            WriteLine("complog replay [OPTIONS] msbuild.complog");
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunGenerated(IEnumerable<string> args)
    {
        string? baseOutputPath = null;
        var options = new FilterOptionSet(analyzers: true)
        {
            { "o|out=", "path to emit to ", void (string b) => baseOutputPath = b },
        };

        try
        {
            var extra = options.Parse(args);
            if (options.Help)
            {
                PrintUsage();
                return ExitSuccess;
            }

            if (options.CustomCompilerFilePath is not null && !IsCustomCompiler)
            {
                return RunInContext(["generated", .. args], options.CustomCompilerFilePath);
            }

            baseOutputPath = GetBaseOutputPath(baseOutputPath, "generated");
            WriteLine($"Outputting to {baseOutputPath}");

            using var reader = GetCompilerCallReader(extra, options.BasicAnalyzerKind, checkVersion: true);
            var compilerCalls = ReadAllCompilerCalls(reader, options.FilterCompilerCalls);
            var compilerCallNames = GetCompilerCallNames(compilerCalls);
            var succeeded = true;

            for (int i = 0; i < compilerCalls.Count; i++)
            {
                var compilerCall = compilerCalls[i];
                var compilationData = reader.ReadCompilationData(compilerCall);

                Write($"{compilerCall.GetDiagnosticName()} ... ");
                var generatedTrees = compilationData.GetGeneratedSyntaxTrees(out var diagnostics);
                WriteLine($"{generatedTrees.Count} files");
                if (diagnostics.Length > 0)
                {
                    WriteLine("\tDiagnostics");
                    foreach (var diagnostic in diagnostics)
                    {
                        WriteLine(diagnostic.ToString());

                        if (diagnostic.Severity == DiagnosticSeverity.Error)
                        {
                            succeeded = false;
                        }
                    }
                }

                foreach (var generatedTree in generatedTrees)
                {
                    WriteLine($"\t{Path.GetFileName(generatedTree.FilePath)}");
                    var fileRelativePath = generatedTree.FilePath.StartsWith(compilerCall.ProjectDirectory, StringComparison.OrdinalIgnoreCase)
                        ? generatedTree.FilePath.Substring(compilerCall.ProjectDirectory.Length + 1)
                        : Path.GetFileName(generatedTree.FilePath);
                    var outputPath = Path.Combine(baseOutputPath, compilerCallNames[i]);
                    var filePath = Path.Combine(outputPath, fileRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    File.WriteAllText(filePath, generatedTree.ToString());
                }
            }

            return succeeded ? ExitSuccess : ExitFailure;
        }
        catch (OptionException e)
        {
            WriteLine(e.Message);
            PrintUsage();
            return ExitFailure;
        }

        void PrintUsage()
        {
            WriteLine("complog generated [OPTIONS] msbuild.complog");
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunHash(IEnumerable<string> args)
    {
        var (command, rest) = ParseCommand(args);
        if (command is null)
        {
            WriteLine("Need a subcommand");
            PrintUsage();
            return ExitFailure;
        }

        return command.ToLowerInvariant() switch
        {
            "print" => RunHashPrint(rest),
            "export" => RunHashExport(rest),
            "help" => RunHashHelp(),
            _ => RunBadCommand(command)
        };

        int RunBadCommand(string command)
        {
            WriteLine($"{command} is not a valid command");
            PrintUsage();
            return ExitFailure;
        }

        void PrintUsage()
        {
            WriteLine($"""
                complog hash [command] [args]

                Commands
                print         Print the identity hash for the compilation to the console
                export        Write the identity and content hash to disk
                """);
        }

        int RunHashHelp()
        {
            PrintUsage();
            return ExitSuccess;
        }

        static (string? Command, IEnumerable<string> Remaning) ParseCommand(IEnumerable<string> args)
        {
            using var e = args.GetEnumerator();
            if (!e.MoveNext())
            {
                return (null, Enumerable.Empty<string>());
            }

            var command = e.Current;
            return (command, args.Skip(1));
        }
    }

    internal int RunHashPrint(IEnumerable<string> args)
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

            using var reader = GetCompilerCallReader(extra, BasicAnalyzerHost.DefaultKind);
            var compilerCalls = reader.ReadAllCompilerCalls(options.FilterCompilerCalls);
            var compilerCallNames = GetCompilerCallNames(compilerCalls);
            for (int i = 0; i < compilerCalls.Count; i++)
            {
                var compilerCall = compilerCalls[i];
                var name = compilerCallNames[i];
                var compilationData = reader.ReadCompilationData(compilerCall);
                var identityHash = compilationData.GetIdentityHash();

                WriteLine($"{name} {identityHash}");
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
            WriteLine("complog hash print [OPTIONS] msbuild.complog");
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunHashExport(IEnumerable<string> args)
    {
        var inline = false;
        var baseOutputPath = "";
        var options = new FilterOptionSet()
        {
            { "i|inline", "put response files next to the project file", i => inline = i != null },
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

            if (inline && !string.IsNullOrEmpty(baseOutputPath))
            {
                WriteLine("Cannot specify both --inline and --out");
                PrintUsage();
                return ExitFailure;
            }

            using var reader = GetCompilerCallReader(extra, BasicAnalyzerHost.DefaultKind);
            if (inline)
            {
                WriteLine($"Generating hash files inline");
            }
            else
            {
                baseOutputPath = GetBaseOutputPath(baseOutputPath, "id");
                WriteLine($"Generating hash files in {baseOutputPath}");
                Directory.CreateDirectory(baseOutputPath);
            }

            var compilerCalls = reader.ReadAllCompilerCalls(options.FilterCompilerCalls);
            var compilerCallNames = GetCompilerCallNames(compilerCalls);
            for (int i = 0; i < compilerCalls.Count; i++)
            {
                var compilerCall = compilerCalls[i];
                var compilationData = reader.ReadCompilationData(compilerCall);
                var (contentHash, identityHash) = compilationData.GetContentAndIdentityHash();

                var dir = inline
                    ? compilerCall.ProjectDirectory
                    : Path.Combine(baseOutputPath, compilerCallNames[i]);
                Directory.CreateDirectory(dir);
                var (contentFileName, identityFileName) = GetFileNames();

                File.WriteAllText(Path.Combine(dir, contentFileName), contentHash);
                File.WriteAllText(Path.Combine(dir, identityFileName), identityHash);

                (string ContentFileName, string IdentityFileName) GetFileNames()
                {
                    const string simpleContentFileName = "build-content-hash.txt";
                    const string simpleIdentityFileName = "build-identity-hash.txt";
                    if (inline)
                    {
                        return IsSingleTarget(compilerCall, compilerCalls)
                            ? (simpleContentFileName, simpleIdentityFileName)
                            : ($"build-{compilerCall.TargetFramework}-content-hash.txt", $"build-{compilerCall.TargetFramework}-identity-hash.txt");
                    }

                    return (simpleContentFileName, simpleIdentityFileName);
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
            WriteLine("complog hash export [OPTIONS] msbuild.complog");
            options.WriteOptionDescriptions(OutputWriter);
        }
    }

    internal int RunBadCommand(string command)
    {
        WriteLine(@$"""{command}"" is not a valid command");
        _ = RunHelp(null);
        return ExitFailure;
    }

    internal int RunHelp(IEnumerable<string>? args)
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

        WriteLine($"""
            complog [command] [args]
            version: {ToolVersion}

            Commands
            create        Create a compiler log file
            replay        Replay compilations from the log
            export        Export compilation contents, rsp and build files to disk
            rsp           Generate compiler response file projects on this machine
            ref           Copy all references and analyzers to a single directory
            analyzers     Print analyzers / generators used by a compilation
            generated     Get generated files for the compilation
            hash          Get the compilation hashes
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

    internal List<CompilerCall> ReadAllCompilerCalls(ICompilerCallReader reader, Func<CompilerCall, bool>? predicate)
    {
        var compilerCalls = reader.ReadAllCompilerCalls(predicate);
        if (compilerCalls.Count == 0)
        {
            var allCompilerCalls = reader.ReadAllCompilerCalls();
            if (allCompilerCalls.Count > 0)
            {
                WriteLine("No compilations found matching filter");
            }
            else
            {
                WriteLine("No compilations found in the log");
            }
        }

        return compilerCalls;
    }

    internal CompilerLogReader GetCompilerLogReader(Stream compilerLogStream, bool leaveOpen, BasicAnalyzerKind? basicAnalyzerKind = null, bool checkVersion = false)
    {
        var reader = CompilerLogReader.Create(compilerLogStream, basicAnalyzerKind, state: null, leaveOpen);
        OnCompilerCallReader(reader);
        CheckCompilerLogReader(reader, checkVersion);
        return reader;
    }

    internal Stream GetOrCreateCompilerLogStream(List<string> extra)
    {
        var logFilePath = GetLogFilePath(extra);
        return CompilerLogUtil.GetOrCreateCompilerLogStream(logFilePath);
    }

    internal ICompilerCallReader GetCompilerCallReader(List<string> extra, BasicAnalyzerKind? basicAnalyzerKind = null, bool checkVersion = false, LogReaderState? state = null)
    {
        var logFilePath = GetLogFilePath(extra);
        var reader = CompilerCallReaderUtil.Create(logFilePath, basicAnalyzerKind, state);
        OnCompilerCallReader(reader);
        CheckCompilerLogReader(reader, checkVersion);

        return reader;
    }

    internal void CheckCompilerLogReader(ICompilerCallReader reader, bool checkVersion)
    {
        if (reader is CompilerLogReader logReader && logReader.IsWindowsLog != RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WriteLine($"Compiler log generated on different operating system");
        }

        if (checkVersion)
        {
            var version = typeof(Compilation).Assembly.GetName().Version;
            foreach (var tuple in reader.ReadAllCompilerAssemblies())
            {
                if (tuple.AssemblyName.Version > version)
                {
                    WriteLine($"Compiler in log is newer than complog: {tuple.AssemblyName.Version} > {version}");
                }
            }
        }
    }

    /// <summary>
    /// Returns a path to a .complog or .binlog to be used for processing
    /// </summary>
    internal string GetLogFilePath(List<string> extra, bool includeCompilerLogs = true)
    {
        string? logFilePath;
        bool foundMultiple = false;
        IEnumerable<string> args = Array.Empty<string>();
        string baseDirectory = WorkingDirectory;
        var printFile = false;
        if (extra.Count == 0)
        {
            (logFilePath, foundMultiple) = FindLogFilePath(baseDirectory, includeCompilerLogs);
            printFile = true;
        }
        else
        {
            logFilePath = extra[0];
            args = extra.Skip(1);
            if (string.IsNullOrEmpty(Path.GetExtension(logFilePath)) && Directory.Exists(logFilePath))
            {
                baseDirectory = logFilePath;
                (logFilePath, foundMultiple) = FindLogFilePath(baseDirectory, includeCompilerLogs);
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
            if (foundMultiple)
            {
                WriteLine($"Found multiple log files in {baseDirectory}");
            }

            WriteLine($"Using {logFilePath}");
        }

        switch (Path.GetExtension(logFilePath))
        {
            case ".complog":
            case ".binlog":
            case ".zip":
                if (args.Any())
                {
                    throw new OptionException($"Extra arguments: {string.Join(' ', args.Skip(1))}", "log");
                }

                return GetResolvedPath(WorkingDirectory, logFilePath);
            case ".sln":
            case ".csproj":
            case ".vbproj":
                return GetLogFilePathAfterBuild(AppDataDirectory, baseDirectory, logFilePath, args);
            default:
                throw new OptionException($"Not a valid log file {logFilePath}", "log");
        }

        static (string? FilePath, bool FoundMultiple) FindLogFilePath(string baseDirectory, bool includeCompilerLogs = true) =>
            includeCompilerLogs
                ? FindFirstFileWithPattern(baseDirectory, "*.complog", "*.binlog", "*.sln", "*.csproj", ".vbproj")
                : FindFirstFileWithPattern(baseDirectory, "*.binlog", "*.sln", "*.csproj", ".vbproj");

        string GetLogFilePathAfterBuild(string appDataDirectory, string baseDirectory, string buildFileName, IEnumerable<string> buildArgs)
        {
            Directory.CreateDirectory(appDataDirectory);
            var binlogFilePath = Path.Combine(appDataDirectory, "build.binlog");
            var buildFilePath = GetResolvedPath(baseDirectory, buildFileName);
            var tag = buildArgs.Any() ? "" : "-t:Rebuild";
            var args = $"build {buildFilePath} -bl:\"{binlogFilePath}\" -nr:false {tag} {string.Join(' ', buildArgs)}";
            WriteLine($"Building {buildFilePath}");
            WriteLine($"dotnet {args}");
            var result = DotnetUtil.Command(args, baseDirectory);
            WriteLine(result.StandardOut);
            WriteLine(result.StandardError);
            if (!result.Succeeded)
            {
                WriteLine("Build Failed!");
            }

            return binlogFilePath;
        }

        static OptionException CreateOptionException() => new("Need a file to analyze", "log");
    }

    internal static (string? FilePath, bool FoundMultiple) FindFirstFileWithPattern(string baseDirectory, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            using var e = Directory
                .EnumerateFiles(baseDirectory, pattern)
                .OrderBy(x => Path.GetFileName(x), PathUtil.Comparer)
                .GetEnumerator();
            if (e.MoveNext())
            {
                return (e.Current, e.MoveNext());
            }
        }

        return (null, false);
    }

    internal string GetBaseOutputPath(string? baseOutputPath, string? directoryName = null)
    {
        if (string.IsNullOrEmpty(baseOutputPath))
        {
            baseOutputPath = ".complog";
            if (directoryName is not null)
            {
                baseOutputPath = Path.Combine(baseOutputPath, directoryName);
            }
        }

        if (!Path.IsPathRooted(baseOutputPath))
        {
            baseOutputPath = Path.Combine(WorkingDirectory, baseOutputPath);
        }

        return baseOutputPath;
    }

    // Is the project for this <see cref="CompilerCall"/> only occur once in the list as a
    // non-satellite assembly?
    internal static bool IsSingleTarget(CompilerCall compilerCall, List<CompilerCall> compilerCalls)
    {
        return compilerCalls.Count(x =>
            x.ProjectFilePath == compilerCall.ProjectFilePath &&
            x.Kind == CompilerCallKind.Regular) == 1;
    }

    // Convert the CompilerCall instances into a list of unique names that are
    // valid file names
    internal static List<string> GetCompilerCallNames(List<CompilerCall> compilerCalls)
    {
        var hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var compilerCall in compilerCalls)
        {
            var name = GetName(compilerCall, compilerCalls);
            if (!hashSet.Add(name))
            {
                var suffix = 1;
                string newName;
                do
                {
                    newName = $"{name}-{suffix}";
                    suffix++;
                } while (!hashSet.Add(newName));

                name = newName;
            }

            list.Add(name);
        }

        return list;

        string GetName(CompilerCall compilerCall, List<CompilerCall> compilerCalls)
        {
            var name = Path.GetFileNameWithoutExtension(compilerCall.ProjectFileName);
            return compilerCall.Kind switch
            {
                CompilerCallKind.Regular => string.IsNullOrEmpty(compilerCall.TargetFramework) || IsSingleTarget(compilerCall, compilerCalls)
                    ? name
                    : $"{name}-{compilerCall.TargetFramework}",
                _ => $"{name}-{compilerCall.Kind.ToString().ToLowerInvariant()}",
            };
        }
    }

    internal static string GetResolvedPath(string baseDirectory, string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.Combine(baseDirectory, path);
    }

    /// <summary>
    /// This is the helper method that is used to transition from the primary <see cref="AssemblyLoadContext"/>
    /// to the custom one.
    /// </summary>
    private static int RunInContextHelper(
        string workingDirectory,
        string appDataDirectory,
        Action<string?> onOutput,
        string[] args)
    {
        var complogApp = new CompilerLogApp(
            workingDirectory,
            appDataDirectory,
            new ForwardingTextWriter(onOutput))
        {
            IsCustomCompiler = true
        };

        return complogApp.Run(args);
    }

    private int RunInContext(string[] args, string userCompilerPath)
    {
        var compilerDirectory = GetCompilerDirectory(userCompilerPath);
        if (!Directory.Exists(compilerDirectory) ||
            !Path.Exists(Path.Combine(compilerDirectory, "Microsoft.CodeAnalysis.dll")))
        {
            throw new Exception($"Invalid compiler directory {compilerDirectory}");
        }

        var alc = new CustomCompilerLoadContext(compilerDirectory, Path.GetDirectoryName(typeof(CompilerLogApp).Assembly.Location)!);
        var type = typeof(CompilerLogApp);
        var assembly = alc.LoadFromAssemblyName(type.Assembly.GetName());
        var contextType = assembly.GetType(type.FullName!, throwOnError: true);
        var method = contextType!.GetMethod(nameof(RunInContextHelper), BindingFlags.Static | BindingFlags.NonPublic);
        try
        {
            object[] contextArgs =
            [
                WorkingDirectory,
                AppDataDirectory,
                (Action<string?>)OnOutput,
                args
            ];
            var ret = method!.Invoke(null, contextArgs)!;
            return (int)ret;
        }
        catch (TargetInvocationException e)
        {
            throw e.InnerException!;
        }

        void OnOutput(string? output) => OutputWriter.WriteLine(output);

        static string GetCompilerDirectory(string userCompilerPath)
        {
            if (Path.GetExtension(userCompilerPath) == ".dll")
            {
                // If the user specified a dll, we assume it is the compiler
                // and return the directory of that dll.
                return Path.GetDirectoryName(userCompilerPath)!;
            }

            return userCompilerPath;
        }
    }

    internal void Write(string str) => OutputWriter.Write(str);
    internal void WriteLine(string line) => OutputWriter.WriteLine(line);
}
