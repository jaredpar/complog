using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Configuration.Internal;
using System.Linq;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Tool to export compilations to disk for other uses
/// </summary>
public sealed class ExportUtil
{
    private sealed class ContentBuilder
    {
        /// <summary>
        /// Content can exist outside the cone of the original project tree. That content 
        /// is mapped, by original directory name, to a new directory in the output. This
        /// holds the map from the old directory to the new one.
        /// </summary>
        private readonly Dictionary<string, string> MiscMap = new(PathUtil.Comparer);

        internal string DestinationDirectory { get; }
        internal string SourceDirectory { get; }
        internal string MiscDirectory { get; }
        internal string EmbeddedResourceDirectory { get; }
        internal string OriginalProjectFilePath { get; }
        internal string OriginalProjectDirectory { get; }
        internal string ProjectName { get; }

        internal ContentBuilder(string destinationDirectory, string originalProjectFilePath)
        {
            DestinationDirectory = destinationDirectory;
            OriginalProjectFilePath = originalProjectFilePath;
            OriginalProjectDirectory = Path.GetDirectoryName(OriginalProjectFilePath)!;
            ProjectName = Path.GetFileName(OriginalProjectFilePath);
            SourceDirectory = Path.Combine(destinationDirectory, "src");
            MiscDirectory = Path.Combine(destinationDirectory, "misc");
            EmbeddedResourceDirectory = Path.Combine(destinationDirectory, "resources");
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(MiscDirectory);
            Directory.CreateDirectory(EmbeddedResourceDirectory);
        }

        private string GetNewFilePath(string originalFilePath)
        {
            string filePath;
            if (originalFilePath.StartsWith(OriginalProjectDirectory, PathUtil.Comparison))
            {
                filePath = PathUtil.ReplacePathStart(originalFilePath, OriginalProjectDirectory, SourceDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            }
            else
            {
                var key = Path.GetDirectoryName(originalFilePath)!;
                if (!MiscMap.TryGetValue(key, out var dirPath))
                {
                    dirPath = Path.Combine(MiscDirectory, $"group{MiscMap.Count}");
                    Directory.CreateDirectory(dirPath);
                    MiscMap[key] = dirPath;
                }

                filePath = Path.Combine(dirPath, Path.GetFileName(originalFilePath));
            }

            return filePath;
        }

        /// <summary>
        /// Writes the content to the new directory structure and returns the full path of the 
        /// file that was written.
        /// </summary>
        internal string WriteContent(string originalFilePath, byte[] content)
        {
            var newFilePath = GetNewFilePath(originalFilePath);
            File.WriteAllBytes(newFilePath, content);
            return newFilePath;
        }

        /// <summary>
        /// Writes the content to the new directory structure and returns the full path of the 
        /// file that was written.
        /// </summary>
        internal string WriteContent(string originalFilePath, Stream stream)
        {
            var newFilePath = GetNewFilePath(originalFilePath);
            using var fileStream = new FileStream(newFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            stream.CopyTo(fileStream);
            return newFilePath;
        }

        /// <summary>
        /// Writes the content to the new directory structure and returns the full path of the 
        /// file that was written.
        /// </summary>
        internal string WriteContent(string originalFilePath, Action<Stream> action)
        {
            var newFilePath = GetNewFilePath(originalFilePath);
            using var fileStream = new FileStream(newFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            action(fileStream);
            return newFilePath;
        }
    }

    public CompilerLogReader Reader { get; }

    public ExportUtil(CompilerLogReader reader)
    {
        Reader = reader;
    }

    public void ExportRsp(CompilerCall compilerCall, string destinationDir, IEnumerable<string> sdkDirectories)
    {
        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        var builder = new ContentBuilder(destinationDir, compilerCall.ProjectFilePath);

        var commandLineList = new List<string>();
        var data = Reader.ReadRawCompilationData(compilerCall);
        Directory.CreateDirectory(destinationDir);
        WriteContent();
        WriteAnalyzers();
        WriteReferences();
        WriteResources();

        var rspFilePath = Path.Combine(destinationDir, "build.rsp");
        File.WriteAllLines(rspFilePath, ProcessRsp());

        // Need to create a few directories so that the builds will actually function
        foreach (var sdkDir in sdkDirectories)
        {
            var cmdFileName = $"build-{Path.GetFileName(sdkDir)}";
            WriteBuildCmd(sdkDir, cmdFileName);
        }

        string? bestSdkDir = sdkDirectories.OrderByDescending(x => x, PathUtil.Comparer).FirstOrDefault();
        if (bestSdkDir is not null)
        {
            WriteBuildCmd(bestSdkDir, "build");
        }

        void WriteBuildCmd(string sdkDir, string cmdFileName)
        {
            var lines = new List<string>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                cmdFileName += ".cmd";
            }
            else
            {
                cmdFileName += ".sh";
                lines.Add(@"#!/bin/sh");
            }

            var execPath = Path.Combine(sdkDir, "Roslyn", "bincore");
            execPath = compilerCall.IsCSharp
                ? Path.Combine(execPath, "csc.dll")
                : Path.Combine(execPath, "vbc.dll");

            lines.Add($@"dotnet exec ""{execPath}"" @build.rsp");
            var cmdFilePath = Path.Combine(destinationDir, cmdFileName);
            File.WriteAllLines(cmdFilePath, lines);

            Console.WriteLine($"Writing {cmdFilePath}");
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var info = new FileInfo(cmdFilePath);
                info.UnixFileMode |= UnixFileMode.UserExecute;
            }
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

                // These options are all rewritten below
                if (span.StartsWith("reference", comparison) ||
                    span.StartsWith("analyzer", comparison) ||
                    span.StartsWith("additionalfile", comparison) ||
                    span.StartsWith("analyzerconfig", comparison) ||
                    span.StartsWith("embed", comparison) ||
                    span.StartsWith("resource", comparison) ||
                    span.StartsWith("linkresource", comparison) ||
                    span.StartsWith("keyfile", comparison))
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
                        var argPath = Path.Combine("obj", argName, Path.GetFileName(tempDir));
                        var argFullPath = Path.Combine(destinationDir, argPath);
                        var isDir = string.IsNullOrEmpty(Path.GetExtension(tempDir));
                        Directory.CreateDirectory(isDir
                            ? argFullPath
                            : Path.GetDirectoryName(argFullPath)!);
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

        void WriteReferences()
        {
            var refDir = Path.Combine(destinationDir, "ref");
            Directory.CreateDirectory(refDir);

            foreach (var tuple in data.References)
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

        void WriteAnalyzers()
        {
            foreach (var analyzer in data.Analyzers)
            {
                using var analyzerStream = Reader.GetAssemblyStream(analyzer.Mvid);
                var filePath = builder.WriteContent(analyzer.FilePath, analyzerStream);

                var arg = $@"/analyzer:""{PathUtil.RemovePathStart(filePath, builder.DestinationDirectory)}""";
                commandLineList.Add(arg);
            }
        }

        void WriteContent()
        {
            foreach (var tuple in data.Contents)
            {
                using var contentStream = Reader.GetContentStream(tuple.ContentHash);
                var filePath = builder.WriteContent(tuple.FilePath, contentStream);
                var prefix = tuple.Kind switch
                {
                    RawContentKind.SourceText => "",
                    RawContentKind.AdditionalText => "/additionalfile:",
                    RawContentKind.AnalyzerConfig => "/analyzerconfig:",
                    RawContentKind.Embed => "/embed:",
                    RawContentKind.SourceLink => "/sourcelink:",
                    RawContentKind.RuleSet => "/ruleset:",
                    RawContentKind.AppConfig => "/appconfig:",
                    RawContentKind.Win32Manifest => "/win32manifest:",
                    RawContentKind.Win32Resource => "/win32res:",
                    RawContentKind.Win32Icon => "/win32icon:",
                    RawContentKind.CryptoKeyFile => "/keyfile:",
                    _ => throw new Exception(),
                };

                commandLineList.Add($@"{prefix}""{PathUtil.RemovePathStart(filePath, builder.DestinationDirectory)}""");
            }
        }

        void WriteResources()
        {
            foreach (var resourceData in data.Resources)
            {
                // The name of file resources isn't that important. It doesn't contribute to the compilation 
                // output. What is important is all the other parts of the string. Just need to create a
                // unique name inside the embedded resource folder
                var d = resourceData.ResourceDescription;
                var originalFileName = d.GetFileName();
                var resourceName = d.GetResourceName();
                var filePath = Path.Combine(builder.EmbeddedResourceDirectory, resourceData.ContentHash, originalFileName ?? resourceName);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllBytes(filePath, Reader.GetContentBytes(resourceData.ContentHash));

                var accessibility = d.IsPublic() ? "public" : "private";
                var kind = originalFileName is null ? "/resource:" : "/linkresource";
                var arg = $"{kind}{PathUtil.RemovePathStart(filePath, builder.DestinationDirectory)},{resourceName},{accessibility}";
                commandLineList.Add(arg);
            }
        }
    }
}
