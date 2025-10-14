using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using NaturalSort.Extension;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System.Diagnostics;

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

            // Normalize out all of the ..\ and .\ in the path
            var normalizedPath = PathNormalizationUtil.NormalizePath(path);
            var fullNormalizedPath = Path.GetFullPath(normalizedPath!);

            string filePath;
            if (fullNormalizedPath.StartsWith(SourceDirectory, PathUtil.Comparison))
            {
                filePath = PathUtil.ReplacePathStart(fullNormalizedPath, SourceDirectory, SourceOutputDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            }
            else
            {
                return MiscDirectory.GetNewFilePath(fullNormalizedPath);
            }

            return filePath;
        }

        [return: NotNullIfNotNull("path")]
        internal override string? NormalizePath(string? path, RawContentKind kind) => kind switch
        {
            RawContentKind.GeneratedText => GeneratedCodeDirectory.GetNewFilePath(path!),
            _ => NormalizePath(path),
        };

        internal override bool IsPathRooted([NotNullWhen(true)] string? path) => PathNormalizationUtil.IsPathRooted(path);

        internal override string RootFileName(string fileName) => PathNormalizationUtil.RootFileName(fileName);
    }

    internal static Regex OptionsRegex { get; } = GetOptionRegex();

    public CompilerLogReader Reader { get; }
    public bool ExcludeAnalyzers { get; }
    internal PathNormalizationUtil PathNormalizationUtil => Reader.PathNormalizationUtil;

    public ExportUtil(CompilerLogReader reader, bool excludeAnalyzers = true)
    {
        Reader = reader;
        ExcludeAnalyzers = excludeAnalyzers;
    }

    public void ExportAll(string destinationDir, IEnumerable<string> sdkDirectories, Func<CompilerCall, bool>? predicate = null)
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

    public void Export(CompilerCall compilerCall, string destinationDir, IEnumerable<string> sdkDirectories)
    {
        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        var originalSourceDirectory = GetSourceDirectory(Reader, compilerCall);
        var builder = new ContentBuilder(destinationDir, originalSourceDirectory, PathNormalizationUtil);
        var commandLineList = new List<string>();
        bool hasNoConfigOption = false;
        var checksumAlgorithm = Reader.GetChecksumAlgorithm(compilerCall);

        try
        {
            Reader.PathNormalizationUtil = builder;
            Directory.CreateDirectory(destinationDir);
            WriteGeneratedFiles();
            WriteEmbedLines();
            WriteContent();
            WriteAnalyzers();
            WriteReferences();
            WriteResources();
            WriteIncludedRulesets();

            var rspFilePath = Path.Combine(destinationDir, "build.rsp");
            File.WriteAllLines(rspFilePath, ProcessRsp());

            // Need to create a few directories so that the builds will actually function
            foreach (var sdkDir in sdkDirectories)
            {
                var cmdFileName = $"build-{Path.GetFileName(sdkDir)}";
                WriteBuildCmd(sdkDir, cmdFileName);
            }

            string? bestSdkDir = sdkDirectories.OrderByDescending(x => x, PathUtil.Comparer.WithNaturalSort()).FirstOrDefault();
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

        List<string> ProcessRsp()
        {
            var arguments = compilerCall.GetArguments();
            var lines = new List<string>(capacity: arguments.Count);

            // compiler options aren't case sensitive
            var comparison = StringComparison.OrdinalIgnoreCase;

            foreach (var line in arguments)
            {
                // The only non-options are source files and those are rewritten by other
                // methods and added to commandLineList
                if (!IsOption(line.AsSpan()))
                {
                    continue;
                }

                var span = line.AsSpan().Slice(1);

                if (span.Equals("noconfig".AsSpan(), comparison))
                {
                    hasNoConfigOption = true;
                    continue;
                }

                // These options are all rewritten below
                if (span.StartsWith("reference", comparison) ||
                    span.StartsWith("analyzer", comparison) ||
                    span.StartsWith("additionalfile", comparison) ||
                    span.StartsWith("analyzerconfig", comparison) ||
                    span.StartsWith("embed", comparison) ||
                    span.StartsWith("resource", comparison) ||
                    span.StartsWith("linkresource", comparison) ||
                    span.StartsWith("sourcelink", comparison) ||
                    span.StartsWith("ruleset", comparison) ||
                    span.StartsWith("keyfile", comparison) ||
                    span.StartsWith("link", comparison))
                {
                    continue;
                }

                // Map all of the output items to the build output directory
                if (span.StartsWith("out", comparison) ||
                    span.StartsWith("refout", comparison) ||
                    span.StartsWith("doc", comparison) ||
                    span.StartsWith("generatedfilesout", comparison) ||
                    span.StartsWith("errorlog", comparison))
                {
                    var index = span.IndexOf(':');
                    var argName = span.Slice(0, index).ToString();
                    var argValue = span.Slice(index + 1);

                    // Handle `/errorlog:"path,version=123"`.
                    ReadOnlySpan<char> path = argValue;
                    var suffix = "";
                    if (span.StartsWith("errorlog", comparison) &&
                        argValue.IndexOf(',') is var commaIndex and >= 0)
                    {
                        // Remove quotes.
                        if (argValue is ['"', .., '"'])
                        {
                            argValue = argValue[1..^1];
                        }

                        path = argValue[0..commaIndex];
                        suffix = argValue[commaIndex..].ToString();
                    }

                    var originalPath = PathNormalizationUtil.NormalizePath(path.ToString());
                    var newPath = builder.BuildOutput.GetNewFilePath(originalPath);
                    commandLineList.Add($@"/{argName}:{FormatPathArgument(newPath + suffix)}");

                    if (span.StartsWith("generatedfilesout", comparison))
                    {
                        Directory.CreateDirectory(newPath);
                    }

                    continue;
                }

                lines.Add(line);
            }

            lines.AddRange(commandLineList);
            return lines;
        }

        // Write these contents to disk and return the new file path
        string WriteRawContent(RawContent rawContent)
        {
            var filePath = Reader.PathNormalizationUtil.NormalizePath(rawContent.FilePath, rawContent.Kind);

            if (rawContent.ContentHash is not null)
            {
                using var contentStream = Reader.GetContentStream(rawContent.Kind, rawContent.ContentHash);
                contentStream.WriteTo(filePath);
            }

            return filePath;
        }

        void WriteReferences()
        {
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
                        var arg = $@"/reference:{alias}=""{PathUtil.RemovePathStart(filePath, destinationDir)}""";
                        commandLineList.Add(arg);
                    }
                }
                else if (pack.EmbedInteropTypes)
                {
                    var arg = $@"/link:""{PathUtil.RemovePathStart(filePath, destinationDir)}""";
                    commandLineList.Add(arg);
                }
                else
                {
                    var arg = $@"/reference:""{PathUtil.RemovePathStart(filePath, destinationDir)}""";
                    commandLineList.Add(arg);
                }
            }
        }

        void WriteAnalyzers()
        {
            if (ExcludeAnalyzers)
            {
                return;
            }

            foreach (var analyzer in Reader.ReadAllAnalyzerData(compilerCall))
            {
                var filePath = builder.AnalyzerDirectory.GetNewFilePath(analyzer.FilePath);
                using var analyzerStream = Reader.GetAssemblyStream(analyzer.Mvid);
                analyzerStream.WriteTo(filePath);
                var arg = $@"/analyzer:""{PathUtil.RemovePathStart(filePath, builder.DestinationDirectory)}""";
                commandLineList.Add(arg);
            }
        }

        void WriteContent()
        {
            foreach (var rawContent in Reader.ReadAllRawContent(compilerCall))
            {
                var prefix = rawContent.Kind switch
                {
                    RawContentKind.SourceText => "",
                    RawContentKind.GeneratedText => null,
                    RawContentKind.AdditionalText => "/additionalfile:",
                    RawContentKind.AnalyzerConfig => "/analyzerconfig:",
                    RawContentKind.Embed => "/embed:",
                    RawContentKind.EmbedLine => null,
                    RawContentKind.SourceLink => "/sourcelink:",
                    RawContentKind.RuleSet => "/ruleset:",
                    RawContentKind.RuleSetInclude => null,
                    RawContentKind.AppConfig => "/appconfig:",
                    RawContentKind.Win32Manifest => "/win32manifest:",
                    RawContentKind.Win32Resource => "/win32res:",
                    RawContentKind.Win32Icon => "/win32icon:",
                    RawContentKind.CryptoKeyFile => "/keyfile:",
                    _ => throw new Exception(),
                };

                if (prefix is null)
                {
                    continue;
                }

                var filePath = WriteRawContent(rawContent);
                commandLineList.Add($@"{prefix}{FormatPathArgument(filePath)}");
            }
        }

        void WriteGeneratedFiles()
        {
            foreach (var rawContent in Reader.ReadAllRawContent(compilerCall, RawContentKind.GeneratedText))
            {
                // Write out the generated text, even if it's not a part of the compilation. This makes it easier
                // for users to see everything that was generated.
                var filePath = WriteRawContent(rawContent);

                if (ExcludeAnalyzers)
                {
                    commandLineList.Add(FormatPathArgument(filePath));
                }
            }
        }

        void WriteIncludedRulesets()
        {
            foreach (var rawContent in Reader.ReadAllRawContent(compilerCall, RawContentKind.RuleSetInclude))
            {
                WriteRawContent(rawContent);
            }
        }

        void WriteEmbedLines()
        {
            foreach (var rawContent in Reader.ReadAllRawContent(compilerCall, RawContentKind.EmbedLine))
            {
                WriteRawContent(rawContent);
            }
        }

        void WriteResources()
        {
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
                var arg = $"{kind}{PathUtil.RemovePathStart(filePath, builder.DestinationDirectory)},{resourceName},{accessibility}";
                commandLineList.Add(arg);
            }
        }

        string FormatPathArgument(string filePath)
        {
            filePath = PathUtil.RemovePathStart(filePath, destinationDir);
            return MaybeQuoteArgument(filePath);
        }
        }

    public static void ExportRsp(CompilerCall compilerCall, TextWriter writer, bool singleLine = false) =>
        ExportRsp(compilerCall.GetArguments(), writer, singleLine);

    public static void ExportRsp(IEnumerable<string> arguments, TextWriter writer, bool singleLine = false)
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
        if (IsOption(arg.AsSpan()))
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

    private static bool IsOption(ReadOnlySpan<char> str) => OptionsRegex.IsMatch(str);

    /// <summary>
    /// Options start with a slash, then contain a colon and continue with a path,
    /// or end with +/- or nothing. Examples:
    /// <list type="bullet">
    /// <item><c>/ref:...</c></item>
    /// <item><c>/unsafe+</c></item>
    /// <item><c>/checked-</c></item>
    /// <item><c>/noconfig</c></item>
    /// </list>
    /// </summary>
    /* lang=regex */
    private const string OptionRegexContent = @"^/[a-z0-9]+(:|[+-]?$)";

#if NET
    [GeneratedRegex(OptionRegexContent, RegexOptions.IgnoreCase)]
    private static partial Regex GetOptionRegex();
#else
    private static Regex GetOptionRegex() => new Regex(OptionRegexContent, RegexOptions.IgnoreCase);
#endif

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
