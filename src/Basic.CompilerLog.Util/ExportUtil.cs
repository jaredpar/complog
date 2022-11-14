using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Tool to export compilations to disk for other uses
/// </summary>
public sealed class ExportUtil
{
    public CompilerLogReader Reader { get; }

    public ExportUtil(CompilerLogReader reader)
    {
        Reader = reader;
    }

    public void ExportRsp(CompilerCall compilerCall, string destinationDir, IEnumerable<string> sdkDirectories)
    {
        // TODO: add path map support

        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        var commandLineList = new List<string>();
        var data = Reader.ReadRawCompilationData(compilerCall);
        Directory.CreateDirectory(destinationDir);
        WriteContent(destinationDir, compilerCall, data, commandLineList);
        WriteReferences(destinationDir, compilerCall, data, commandLineList);
        WriteAnalyzers(destinationDir, compilerCall, data, commandLineList);

        var rspFilePath = Path.Combine(destinationDir, "build.rsp");
        File.WriteAllLines(rspFilePath, ProcessRsp());

        // Need to create a few directories so that the builds will actually function
        foreach (var sdkDir in sdkDirectories)
        {
            var cmdFileName = $"build-{Path.GetFileName(sdkDir)}.cmd";
            WriteBuildCmd(sdkDir, cmdFileName);
        }

        string? bestSdkDir = sdkDirectories.OrderByDescending(x => x, PathUtil.Comparer).FirstOrDefault();
        if (bestSdkDir is not null)
        {
            WriteBuildCmd(bestSdkDir, "build.cmd");
        }

        void WriteBuildCmd(string sdkDir, string cmdFileName)
        {
            var execPath = Path.Combine(sdkDir, @"Roslyn\bincore");
            execPath = compilerCall.IsCSharp
                ? Path.Combine(execPath, "csc.dll")
                : Path.Combine(execPath, "vbc.dll");

            File.WriteAllLines(
                Path.Combine(destinationDir, cmdFileName),
                new[] { $@"dotnet exec ""{execPath}"" @build.rsp" });
        }

        List<string> ProcessRsp()
        {
            var lines = new List<string>();

            // compiler options aren't case sensitive
            var comparison = StringComparison.OrdinalIgnoreCase;

            foreach (var line in compilerCall.Arguments)
            {
                // The only non-options are source files and those are rewritten by this 
                // process
                if (!IsOption(line))
                {
                    continue;
                }

                var span = line.AsSpan().Slice(1);
                if (span.StartsWith("reference", comparison) ||
                    span.StartsWith("analyzer", comparison) ||
                    span.StartsWith("additionalfile", comparison) ||
                    span.StartsWith("analyzerconfig", comparison))
                {
                    continue;
                }

                // The round trip logic does not yet handle these type of embeds
                // https://github.com/jaredpar/basic-compiler-logger/issues/6
                if (span.StartsWith("embed", comparison) ||
                    span.StartsWith("resource", comparison) ||
                    span.StartsWith("sourcelink", comparison) ||
                    span.StartsWith("keyfile", comparison) ||
                    span.StartsWith("publicsign", comparison))
                {
                    continue;
                }

                // Need to pre-create the output directories to allow the compiler to execute
                if (span.StartsWith("out", comparison) ||
                    span.StartsWith("refout", comparison) ||
                    span.StartsWith("doc", comparison))
                {
                    var index = span.IndexOf(':');
                    var tempDir = span.Slice(index + 1).ToString();

                    // The RSP can write out full paths in some cases for these items, rewrite them to local
                    // outside of obj 
                    if (Path.IsPathRooted(tempDir))
                    {
                        var argName = span.Slice(0, index).ToString();
                        var argPath = Path.Combine(destinationDir, "obj", argName, Path.GetFileName(tempDir));
                        var isDir = string.IsNullOrEmpty(Path.GetExtension(tempDir));
                        Directory.CreateDirectory(isDir
                            ? argPath
                            : Path.GetDirectoryName(argPath)!);
                        commandLineList.Add($@"/{argName}:""{argPath}""");
                        continue;
                    }
                    else
                    {
                        tempDir = Path.Combine(destinationDir, span.Slice(index + 1).ToString());
                        tempDir = Path.GetDirectoryName(tempDir);
                        Directory.CreateDirectory(tempDir!);
                    }

                }

                lines.Add(line);
            }

            lines.AddRange(commandLineList);
            return lines;

            static bool IsOption(string str) =>
                str.Length > 0 && str[0] is '-' or '/';
        }
    }

    private void WriteReferences(string destinationDir, CompilerCall compilerCall, RawCompilationData rawCompilationData, List<string> commandLineList)
    {
        var refDir = Path.Combine(destinationDir, "ref");
        Directory.CreateDirectory(refDir);

        foreach (var tuple in rawCompilationData.References)
        {
            var mvid = tuple.Mvid;
            var filePath = Path.Combine(refDir, Reader.GetMetadataReferenceFileName(mvid));

            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Reader.CopyAssemblyBytes(mvid, fileStream);

            // TODO: need to support aliases here.
            var arg = $@"/reference:""{PathUtil.RemovePathStart(filePath, destinationDir)}""";
            commandLineList.Add(arg);
        }
    }

    private void WriteAnalyzers(string destinationDir, CompilerCall compilerCall, RawCompilationData rawCompilationData, List<string> commandLineList)
    {
        var analyzerDir = Path.Combine(destinationDir, "analyzers");
        var map = new Dictionary<string, string>(PathUtil.Comparer);
        foreach (var analyzer in rawCompilationData.Analyzers)
        {
            // Group analyzers by the directory they came from. This will ensure it mimics the 
            // setup of the NuGet they came from as this is how the compiler groups them.
            var dir = Path.GetDirectoryName(analyzer.FilePath)!;
            if (!map.TryGetValue(dir, out var outDir))
            {
                outDir = Path.Combine(analyzerDir, $"group{map.Count}");
                Directory.CreateDirectory(outDir);
                map[dir] = outDir;
            }

            var filePath = Path.Combine(outDir, analyzer.FileName);
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Reader.CopyAssemblyBytes(analyzer.Mvid, fileStream);

            var arg = $@"/analyzer:""{PathUtil.RemovePathStart(filePath, destinationDir)}""";
            commandLineList.Add(arg);
        }
    }

    private void WriteContent(string destinationDir, CompilerCall compilerCall, RawCompilationData rawCompilationData, List<string> commandLineList)
    {
        var projectDir = Path.GetDirectoryName(compilerCall.ProjectFilePath)!;

        // For all content in the cone of the original project
        var srcDir = Path.Combine(destinationDir, "src");

        // For all content outside the cone of the original project
        var miscDir = Path.Combine(destinationDir, "misc");
        var miscMap = new Dictionary<string, string>(PathUtil.Comparer);

        foreach (var tuple in rawCompilationData.Contents)
        {
            string filePath;
            if (tuple.FilePath.StartsWith(projectDir, PathUtil.Comparison))
            {
                filePath = PathUtil.ReplacePathStart(tuple.FilePath, projectDir, srcDir);
            }
            else
            {
                var key = Path.GetDirectoryName(tuple.FilePath)!;
                if (!miscMap.TryGetValue(key, out var dirPath))
                {
                    dirPath = Path.Combine(miscDir, $"group{miscMap.Count}");
                    Directory.CreateDirectory(dirPath);
                    miscMap[key] = dirPath;
                }

                filePath = Path.Combine(dirPath, Path.GetFileName(tuple.FilePath));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Reader.CopyContentTo(tuple.ContentHash, fileStream);

            var prefix = tuple.Kind switch
            {
                RawContentKind.SourceText => "",
                RawContentKind.AdditionalText => "/additionalfile:",
                RawContentKind.AnalyzerConfig => "/analyzerconfig:",
                _ => throw new Exception(),
            };

            commandLineList.Add($@"{prefix}""{PathUtil.RemovePathStart(filePath, destinationDir)}""");
        }
    }
}
