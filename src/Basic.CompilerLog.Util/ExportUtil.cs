using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NuGet.Versioning;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Tool to export compilations to disk for other uses
/// </summary>
public sealed partial class ExportUtil
{
    private sealed class ContentBuilder : PathNormalizationUtil
    {
        /// <summary>
        /// This is the <see cref="PathNormalizationUtil"/> that was used by the <see cref="CompilerLogReader"/>.
        /// </summary>
        private PathNormalizationUtil PathNormalizationUtil { get; }

        /// <summary>
        /// This is the original source directory where the compilation occurred. This is after it went through
        /// <see cref="PathNormalizationUtil.NormalizePath(string?)"/>.
        /// </summary>
        internal string SourceDirectory { get; }

        /// <summary>
        /// The destination directory where all of the content is being written
        /// </summary>
        internal string DestinationDirectory { get; }

        /// <summary>
        /// The location of source files in the destination directory
        /// </summary>
        internal string SourceOutputDirectory { get; }
        internal string EmbeddedResourceDirectory { get; }
        private MiscDirectory MiscDirectory { get; }
        internal ResilientDirectory AnalyzerDirectory { get; }
        internal ResilientDirectory GeneratedCodeDirectory { get; }
        internal ResilientDirectory BuildOutput { get; }

        internal ContentBuilder(string destinationDirectory, string originalSourceDirectory, PathNormalizationUtil pathNormalizationUtil)
        {
            PathNormalizationUtil = pathNormalizationUtil;
            DestinationDirectory = destinationDirectory;
            SourceDirectory = originalSourceDirectory;
            SourceOutputDirectory = Path.Combine(destinationDirectory, "src");
            EmbeddedResourceDirectory = Path.Combine(destinationDirectory, "resources");
            MiscDirectory = new(Path.Combine(destinationDirectory, "misc"));
            GeneratedCodeDirectory = new(Path.Combine(destinationDirectory, "generated"));
            AnalyzerDirectory = new(Path.Combine(destinationDirectory, "analyzers"));
            BuildOutput = new(Path.Combine(destinationDirectory, "output"), flatten: true);
            Directory.CreateDirectory(SourceOutputDirectory);
            Directory.CreateDirectory(EmbeddedResourceDirectory);
        }

        [return: NotNullIfNotNull("path")]
        internal override string? NormalizePath(string? path)
        {
            if (path is null)
            {
                return null;
            }

            // Normalize out all of the ..\ and .\ in the path to the current platform.
            var normalizedPath = PathNormalizationUtil.NormalizePath(path);

            // If the path isn't rooted then it's relative to the source directory. Need to
            // make it relative to the new source directory.
            if (!Path.IsPathRooted(normalizedPath))
            {
                return Path.Combine(Path.GetFileName(SourceOutputDirectory)!, normalizedPath);
            }

            var normalizedFullPath = Path.GetFullPath(normalizedPath);

            if (normalizedFullPath.StartsWith(SourceDirectory, PathUtil.Comparison))
            {
                var exportFilePath = PathUtil.ReplacePathStart(normalizedFullPath, SourceDirectory, SourceOutputDirectory);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(exportFilePath)!);
                return exportFilePath;
            }
            else
            {
                return MiscDirectory.GetNewFilePath(normalizedFullPath);
            }
        }

        [return: NotNullIfNotNull("path")]
        internal override string? NormalizePath(string? path, RawContentKind kind) => kind switch
        {
            RawContentKind.GeneratedText => GeneratedCodeDirectory.GetNewFilePath(path!),
            _ => NormalizePath(path),
        };

        [return: NotNullIfNotNull("path")]
        internal override string? NormalizePath(string path, ReadOnlySpan<char> optionName)
        {
            if (optionName is "out" or "refout" or "doc" or "generatedfilesout" or "errorlog")
            {
                var originalPath = PathNormalizationUtil.NormalizePath(path, optionName);
                var newPath = BuildOutput.GetNewFilePath(originalPath);

                if (optionName is "generatedfilesout")
                {
                    _ = Directory.CreateDirectory(newPath);
                }

               return PathUtil.RemovePathStart(newPath, BuildOutput.DirectoryPath);
            }

            return NormalizePath(path);
        }

        internal override bool IsPathRooted([NotNullWhen(true)] string? path) => PathNormalizationUtil.IsPathRooted(path);

        internal override string RootFileName(string fileName) => PathNormalizationUtil.RootFileName(fileName);
    }

    public CompilerLogReader Reader { get; }
    public bool ExcludeAnalyzers { get; }
    internal PathNormalizationUtil PathNormalizationUtil => Reader.PathNormalizationUtil;

    public ExportUtil(CompilerLogReader reader, bool excludeAnalyzers = true)
    {
        Reader = reader;
        ExcludeAnalyzers = excludeAnalyzers;
    }

    public void ExportAll(string destinationDir, IEnumerable<(string SdkDirectory, NuGetVersion SdkVersion)> sdkDirectories, Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        for (int  i = 0; i < Reader.Count ; i++)
        {
            var compilerCall = Reader.ReadCompilerCall(i);
            if (predicate(compilerCall))
            {
                var dir = Path.Combine(destinationDir, i.ToString());
                Directory.CreateDirectory(dir);
                Export(compilerCall, dir, sdkDirectories);
            }
        }
    }

    public void Export(CompilerCall compilerCall, string destinationDir, IEnumerable<(string SdkDirectory, NuGetVersion SdkVersion)> sdkDirectories)
    {
        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        var originalSourceDirectory = GetSourceDirectory(Reader, compilerCall);
        var builder = new ContentBuilder(destinationDir, originalSourceDirectory, PathNormalizationUtil);
        bool hasNoConfigOption = false;
        var checksumAlgorithm = Reader.GetChecksumAlgorithm(compilerCall);

        try
        {
            Reader.PathNormalizationUtil = builder;
            Directory.CreateDirectory(destinationDir);
            WriteContent();
            var rspReferenceLines = WriteReferences();
            var rspAnalyzerLines = WriteAnalyzers();
            var rspResourceLines = WriteResources();
            var rspLines = CreateRsp(
                rspReferenceLines,
                rspAnalyzerLines,
                rspResourceLines);
            var rspFilePath = Path.Combine(destinationDir, "build.rsp");
            File.WriteAllLines(rspFilePath, rspLines);

            // Need to create a few directories so that the builds will actually function
            foreach (var sdkDir in sdkDirectories)
            {
                var cmdFileName = $"build-{Path.GetFileName(sdkDir.SdkDirectory)}";
                WriteBuildCmd(sdkDir.SdkDirectory, cmdFileName);
            }

            string? bestSdkDir = sdkDirectories.OrderByDescending(x => x.SdkVersion).Select(x => x.SdkDirectory).FirstOrDefault();
            if (bestSdkDir is not null)
            {
                WriteBuildCmd(bestSdkDir, "build");
            }

        }
        finally
        {
            Reader.PathNormalizationUtil = Reader.DefaultPathNormalizationUtil;
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

            var noConfig = hasNoConfigOption
                ? "/noconfig "
                : string.Empty;

            lines.Add($@"dotnet exec ""{execPath}"" {noConfig}@build.rsp");
            var cmdFilePath = Path.Combine(destinationDir, cmdFileName);
            File.WriteAllLines(cmdFilePath, lines);

#if NET

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var info = new FileInfo(cmdFilePath);
                info.UnixFileMode |= UnixFileMode.UserExecute;
            }

#endif
        }

        List<string> CreateRsp(List<string> referenceLines, List<string> analyzerLines, List<string> resourceLines)
        {
            var oldLines = Reader.ReadArguments(compilerCall);
            var newLines = new List<string>(capacity: oldLines.Count);

            // If we're excluding analyzers then we need to add the generated files as inputs
            // to the compilation. The compiler adds generated syntax trees first into the 
            // compilation so replicate that here.
            if (ExcludeAnalyzers)
            {
                foreach (var rawContent in Reader.ReadAllRawContent(compilerCall, RawContentKind.GeneratedText))
                {
                    var filePath = Reader.PathNormalizationUtil.NormalizePath(rawContent.FilePath, rawContent.Kind);
                    newLines.Add(NormalizeSourceFilePath(filePath));
                }
            }

            foreach (var oldLine in oldLines)
            {
                if (CompilerCommandLineUtil.TryParseOption(oldLine, out var option))
                {
                    switch (option.Name)
                    {
                        case "reference" or "r":
                        {
                            newLines.AddRange(referenceLines);
                            referenceLines.Clear();
                            continue;
                        }
                        case "analyzer" or "a":
                        {
                            newLines.AddRange(analyzerLines);
                            analyzerLines.Clear();
                            continue;
                        }
                        case "resource" or "res" or "linkresource" or "linkres":
                        {
                            newLines.AddRange(resourceLines);
                            resourceLines.Clear();
                            continue;
                        }
                        case "noconfig":
                        {
                            hasNoConfigOption = true;
                            continue;
                        }
                    }

                    if (CompilerCommandLineUtil.IsPathOption(option))
                    {
                        var newLine = CompilerCommandLineUtil.NormalizePathOption(option, (p, _) => PathUtil.MaybeRemovePathStart(p, destinationDir));
                        newLines.Add(newLine);
                        continue;
                    }

                    newLines.Add(oldLine);
                }
                else
                {
                    // This is a source file, just clean up the prefix
                    newLines.Add(NormalizeSourceFilePath(oldLine));
                }
            }

            return newLines;

            string NormalizeSourceFilePath(string path)
            {
                path = CompilerCommandLineUtil.MaybeRemoveQuotes(path);
                path = PathUtil.MaybeRemovePathStart(path, destinationDir);
                return CompilerCommandLineUtil.MaybeQuotePath(path);
            }
        }

        List<string> WriteReferences()
        {
            var list = new List<string>();
            var refDir = Path.Combine(destinationDir, "ref");
            Directory.CreateDirectory(refDir);

            foreach (var pack in Reader.ReadAllReferenceData(compilerCall))
            {
                var mvid = pack.Mvid;
                var filePath = Path.Combine(refDir, Reader.GetMetadataReferenceFileName(mvid));

                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                Reader.CopyAssemblyBytes(mvid, fileStream);

                if (pack.Aliases.Length > 0)
                {
                    foreach (var alias in pack.Aliases)
                    {
                        var arg = $@"/reference:{alias}={FormatPathArgument(filePath)}";
                        list.Add(arg);
                    }
                }
                else if (pack.EmbedInteropTypes)
                {
                    var arg = $@"/link:{FormatPathArgument(filePath)}";
                    list.Add(arg);
                }
                else
                {
                    var arg = $@"/reference:{FormatPathArgument(filePath)}";
                    list.Add(arg);
                }
            }

            return list;
        }

        List<string> WriteAnalyzers()
        {
            if (ExcludeAnalyzers)
            {
                return [];
            }

            var list = new List<string>();
            foreach (var analyzer in Reader.ReadAllAnalyzerData(compilerCall))
            {
                var filePath = builder.AnalyzerDirectory.GetNewFilePath(analyzer.FilePath);
                using var analyzerStream = Reader.GetAssemblyStream(analyzer.Mvid);
                analyzerStream.WriteTo(filePath);
                var arg = $@"/analyzer:""{FormatPathArgument(filePath)}""";
                list.Add(arg);
            }

            return list;
        }

        List<string> WriteResources()
        {
            var lines = new List<string>();
            foreach (var resourceData in Reader.ReadAllResourceData(compilerCall))
            {
                // The name of file resources isn't that important. It doesn't contribute to the compilation
                // output. What is important is all the other parts of the string. Just need to create a
                // unique name inside the embedded resource folder
                var d = Reader.ReadResourceDescription(resourceData);
                var originalFileName = d.GetFileName();
                var resourceName = d.GetResourceName();
                var filePath = Path.Combine(builder.EmbeddedResourceDirectory, resourceData.ContentHash, originalFileName ?? resourceName);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllBytes(filePath, Reader.GetContentBytes(resourceData));

                var accessibility = d.IsPublic() ? "public" : "private";
                var kind = originalFileName is null ? "/resource:" : "/linkresource";
                // TODO: how do quotes work with this format?
                var arg = $"{kind}{FormatPathArgument(filePath)},{resourceName},{accessibility}";
                lines.Add(arg);
            }

            return lines;
        }

        // Write out all of the raw content to disk so it can be referenced by the exported 
        // data
        void WriteContent()
        {
            foreach (var rawContent in Reader.ReadAllRawContent(compilerCall))
            {
                if (rawContent.ContentHash is not null)
                {
                    var filePath = Reader.PathNormalizationUtil.NormalizePath(rawContent.FilePath, rawContent.Kind);
                    using var contentStream = Reader.GetContentStream(rawContent.Kind, rawContent.ContentHash);
                    contentStream.WriteTo(filePath);
                }
            }
        }

        string FormatPathArgument(string filePath)
        {
            filePath = PathUtil.RemovePathStart(filePath, destinationDir);
            return CompilerCommandLineUtil.MaybeQuotePath(filePath);
        }
    }

    public static void ExportRsp(IReadOnlyCollection<string> arguments, TextWriter writer, bool singleLine = false)
    {
        bool isFirst = true;
        foreach (var line in arguments)
        {
            var str = MaybeQuoteArgument(line);

            if ("/noconfig".Equals(str, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (singleLine)
            {
                if (!isFirst)
                {
                    writer.Write(' ');
                }
                writer.Write(str);
            }
            else
            {
                writer.WriteLine(str);
            }

            isFirst = false;
        }
    }

    private static string MaybeQuoteArgument(string arg)
    {
        if (CompilerCommandLineUtil.IsOption(arg.AsSpan()))
        {
            return arg;
        }

        if (arg.Contains(' ') || arg.Contains('=') || arg.Contains(','))
        {
            var str = $@"""{arg}""";
            return str;
        }

        return arg;
    }

    /// <summary>
    /// This will return the logical source root for the given compilation. This is the prefix
    /// for files that do _not_ need to be replicated during export. Replicating the rest of the
    /// path is important as it impacts items like .editorconfig layout
    /// </summary>
    internal string GetSourceDirectory(CompilerLogReader reader, CompilerCall compilerCall)
    {
        Debug.Assert(object.ReferenceEquals(reader.DefaultPathNormalizationUtil, reader.PathNormalizationUtil));

        var sourceRootDir = Path.GetDirectoryName(compilerCall.ProjectFilePath)!;
        var checksumAlgorithm = reader.GetChecksumAlgorithm(compilerCall);
        foreach (var content in reader.ReadAllRawContent(compilerCall, RawContentKind.AnalyzerConfig))
        {
            var filePath = reader.PathNormalizationUtil.NormalizePath(content.FilePath);
            var contentDir = Path.GetDirectoryName(filePath)!;
            if (!sourceRootDir.StartsWith(contentDir, PathUtil.Comparison))
            {
                continue;
            }

            if (content.ContentHash is null)
            {
                continue;
            }

            var sourceText = reader.ReadSourceText(content.Kind, content.ContentHash, checksumAlgorithm);
            if (sourceText is not null && RoslynUtil.IsGlobalEditorConfigWithSection(sourceText))
            {
                continue;
            }

            sourceRootDir = contentDir;
        }

        return sourceRootDir;
    }
}
