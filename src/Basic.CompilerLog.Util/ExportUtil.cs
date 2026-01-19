using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Tool to export compilations to disk for other uses
/// </summary>
public sealed partial class ExportUtil
{
    internal sealed class ContentBuilder : PathNormalizationUtil
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

            // If the path isn't rooted then it's relative to the working directory. Need to
            // make it relative to the new working directory
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
                var normalizedPath = PathNormalizationUtil.NormalizePath(path, optionName);
                var newPath = BuildOutput.GetNewFilePath(normalizedPath);

                if (optionName is "generatedfilesout")
                {
                    _ = Directory.CreateDirectory(newPath);
                }

                return Path.IsPathRooted(normalizedPath)
                    ? newPath
                    : PathUtil.RemovePathStart(newPath, DestinationDirectory);
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

    internal void ExportAll(string destinationDir, IEnumerable<(string SdkDirectory, SdkVersion SdkVersion)> sdkDirectories, Func<CompilerCall, bool>? predicate = null)
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

    public void Export(CompilerCall compilerCall, string destinationDir, IEnumerable<(string SdkDirectory, SdkVersion SdkVersion)> sdkDirectories)
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
                        case "reference" or "r" or "link" or "l":
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

    /// <summary>
    /// Exports the compiler log as a solution file and a set of project files that can be built
    /// using standard MSBuild tooling.
    /// </summary>
    /// <param name="destinationDir">The directory to export the solution to</param>
    /// <param name="solutionName">Optional name for the solution file (without extension). Defaults to "export"</param>
    /// <param name="predicate">Optional predicate to filter which compiler calls to include</param>
    /// <returns>The path to the generated solution file</returns>
    public string ExportSolution(string destinationDir, string? solutionName = null, Func<CompilerCall, bool>? predicate = null)
    {
        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        solutionName ??= "export";
        predicate ??= static c => c.Kind == CompilerCallKind.Regular;

        Directory.CreateDirectory(destinationDir);

        // Group compiler calls by project file path to handle multi-targeting
        var projectGroups = new Dictionary<string, List<(CompilerCall Call, int Index)>>(PathUtil.Comparer);
        for (int i = 0; i < Reader.Count; i++)
        {
            var compilerCall = Reader.ReadCompilerCall(i);
            if (predicate(compilerCall))
            {
                if (!projectGroups.TryGetValue(compilerCall.ProjectFilePath, out var list))
                {
                    list = new List<(CompilerCall, int)>();
                    projectGroups[compilerCall.ProjectFilePath] = list;
                }
                list.Add((compilerCall, i));
            }
        }

        // Build a map of compiler call index to project info for project reference resolution
        var indexToProjectInfo = new Dictionary<int, ExportedProjectInfo>();
        var projects = new List<ExportedProjectInfo>();

        foreach (var kvp in projectGroups)
        {
            var projectFilePath = kvp.Key;
            var calls = kvp.Value;
            var firstCall = calls[0].Call;

            // Collect all target frameworks for this project
            var targetFrameworks = calls
                .Select(c => c.Call.TargetFramework)
                .Where(tf => !string.IsNullOrEmpty(tf))
                .Select(tf => tf!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tf => tf, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var projectName = Path.GetFileNameWithoutExtension(projectFilePath);
            var exportedProjectFileName = firstCall.IsCSharp ? $"{projectName}.csproj" : $"{projectName}.vbproj";
            var projectDir = Path.Combine(destinationDir, projectName);
            var exportedProjectFilePath = Path.Combine(projectDir, exportedProjectFileName);

            var projectInfo = new ExportedProjectInfo(
                projectName,
                exportedProjectFilePath,
                projectDir,
                firstCall.IsCSharp,
                targetFrameworks,
                calls);

            projects.Add(projectInfo);
            foreach (var (_, index) in calls)
            {
                indexToProjectInfo[index] = projectInfo;
            }
        }

        // Now generate each project file
        foreach (var projectInfo in projects)
        {
            GenerateProjectFile(projectInfo, indexToProjectInfo);
        }

        // Generate the solution file (.slnx format)
        var solutionFilePath = Path.Combine(destinationDir, $"{solutionName}.slnx");
        GenerateSolutionFile(solutionFilePath, projects);

        return solutionFilePath;
    }

    private void GenerateProjectFile(ExportedProjectInfo projectInfo, Dictionary<int, ExportedProjectInfo> indexToProjectInfo)
    {
        Directory.CreateDirectory(projectInfo.ProjectDirectory);

        // Use the first compiler call to get most of the project info
        var (firstCall, firstIndex) = projectInfo.CompilerCalls[0];
        var compilerCallData = Reader.ReadCompilerCallData(firstCall);

        // Determine output type
        var outputType = compilerCallData.CompilationOptions.OutputKind switch
        {
            OutputKind.ConsoleApplication => "Exe",
            OutputKind.WindowsApplication => "WinExe",
            OutputKind.DynamicallyLinkedLibrary => "Library",
            _ => "Library"
        };

        // Collect references, distinguishing between project refs and external refs
        var projectReferences = new HashSet<ExportedProjectInfo>();
        var externalReferences = new List<ReferenceData>();
        var seenMvids = new HashSet<Guid>();

        foreach (var (call, _) in projectInfo.CompilerCalls)
        {
            foreach (var refData in Reader.ReadAllReferenceData(call))
            {
                if (!seenMvids.Add(refData.Mvid))
                {
                    continue;
                }

                // Check if this reference is the output of another project in the log
                if (Reader.TryGetCompilerCallIndex(refData.Mvid, out var refCompilerCallIndex) &&
                    indexToProjectInfo.TryGetValue(refCompilerCallIndex, out var refProjectInfo))
                {
                    projectReferences.Add(refProjectInfo);
                }
                else if (!IsFrameworkReference(refData.FilePath, refData.AssemblyIdentityData.AssemblyName))
                {
                    externalReferences.Add(refData);
                }
            }
        }

        // Write source files (skip generated code from obj directory)
        var sourceFiles = new List<string>();
        foreach (var (call, _) in projectInfo.CompilerCalls)
        {
            foreach (var sourceTextData in Reader.ReadAllSourceTextData(call))
            {
                if (sourceTextData.SourceTextKind != SourceTextKind.SourceCode)
                {
                    continue;
                }

                var originalPath = sourceTextData.FilePath;

                // Skip files under obj directory - these are generated during build
                if (IsGeneratedFile(originalPath))
                {
                    continue;
                }

                var fileName = Path.GetFileName(originalPath);
                var destPath = Path.Combine(projectInfo.ProjectDirectory, fileName);

                // Handle duplicate file names by using a subdirectory
                if (sourceFiles.Contains(destPath, PathUtil.Comparer))
                {
                    var subDir = Path.Combine(projectInfo.ProjectDirectory, $"src_{sourceFiles.Count}");
                    Directory.CreateDirectory(subDir);
                    destPath = Path.Combine(subDir, fileName);
                }

                if (!File.Exists(destPath))
                {
                    var sourceText = Reader.ReadSourceText(sourceTextData);
                    using var writer = new StreamWriter(destPath, append: false, Encoding.UTF8);
                    sourceText.Write(writer);
                    sourceFiles.Add(destPath);
                }
            }
        }

        // Write reference assemblies that aren't framework references
        var refDir = Path.Combine(projectInfo.ProjectDirectory, "ref");
        if (externalReferences.Count > 0)
        {
            Directory.CreateDirectory(refDir);
        }

        foreach (var refData in externalReferences)
        {
            var refFileName = Reader.GetMetadataReferenceFileName(refData.Mvid);
            var refFilePath = Path.Combine(refDir, refFileName);
            if (!File.Exists(refFilePath))
            {
                using var fileStream = new FileStream(refFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                Reader.CopyAssemblyBytes(refData.Mvid, fileStream);
            }
        }

        // Generate the project file content
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");

        // Target framework(s)
        if (projectInfo.TargetFrameworks.Count == 0)
        {
            sb.AppendLine("    <TargetFramework>net8.0</TargetFramework>");
        }
        else if (projectInfo.TargetFrameworks.Count == 1)
        {
            sb.AppendLine($"    <TargetFramework>{projectInfo.TargetFrameworks[0]}</TargetFramework>");
        }
        else
        {
            sb.AppendLine($"    <TargetFrameworks>{string.Join(";", projectInfo.TargetFrameworks)}</TargetFrameworks>");
        }

        sb.AppendLine($"    <OutputType>{outputType}</OutputType>");
        sb.AppendLine($"    <AssemblyName>{Path.GetFileNameWithoutExtension(compilerCallData.AssemblyFileName)}</AssemblyName>");

        // Add nullable context if applicable for C#
        if (projectInfo.IsCSharp && compilerCallData.CompilationOptions is CSharpCompilationOptions csharpOptions)
        {
            var nullable = csharpOptions.NullableContextOptions switch
            {
                NullableContextOptions.Enable => "enable",
                NullableContextOptions.Warnings => "warnings",
                NullableContextOptions.Annotations => "annotations",
                _ => null
            };
            if (nullable is not null)
            {
                sb.AppendLine($"    <Nullable>{nullable}</Nullable>");
            }

            if (csharpOptions.AllowUnsafe)
            {
                sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            }
        }

        // Disable implicit usings since we're exporting explicit source
        sb.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");

        sb.AppendLine("  </PropertyGroup>");

        // Project references
        if (projectReferences.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var projRef in projectReferences.OrderBy(p => p.ProjectName, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(projectInfo.ProjectDirectory, projRef.ExportedProjectFilePath);
                sb.AppendLine($"    <ProjectReference Include=\"{relativePath}\" />");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        // External references (non-framework)
        if (externalReferences.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var refData in externalReferences.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase))
            {
                var refFileName = Reader.GetMetadataReferenceFileName(refData.Mvid);
                var hintPath = Path.Combine("ref", refFileName);
                sb.Append($"    <Reference Include=\"{refData.AssemblyIdentityData.AssemblyName ?? Path.GetFileNameWithoutExtension(refFileName)}\"");

                if (refData.EmbedInteropTypes)
                {
                    sb.AppendLine(">");
                    sb.AppendLine($"      <HintPath>{hintPath}</HintPath>");
                    sb.AppendLine("      <EmbedInteropTypes>true</EmbedInteropTypes>");
                    sb.AppendLine("    </Reference>");
                }
                else if (refData.Aliases.Length > 0)
                {
                    sb.AppendLine(">");
                    sb.AppendLine($"      <HintPath>{hintPath}</HintPath>");
                    sb.AppendLine($"      <Aliases>{string.Join(",", refData.Aliases)}</Aliases>");
                    sb.AppendLine("    </Reference>");
                }
                else
                {
                    sb.AppendLine(">");
                    sb.AppendLine($"      <HintPath>{hintPath}</HintPath>");
                    sb.AppendLine("    </Reference>");
                }
            }
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine();
        sb.AppendLine("</Project>");

        File.WriteAllText(projectInfo.ExportedProjectFilePath, sb.ToString(), Encoding.UTF8);
    }

    private static void GenerateSolutionFile(string solutionFilePath, List<ExportedProjectInfo> projects)
    {
        var sb = new StringBuilder();

        // Generate .slnx XML format
        sb.AppendLine("<Solution>");

        foreach (var project in projects.OrderBy(p => p.ProjectName, StringComparer.OrdinalIgnoreCase))
        {
            var projectFileName = Path.GetFileName(project.ExportedProjectFilePath);
            var relativePath = Path.Combine(project.ProjectName, projectFileName);
            // Use forward slashes in the path as per .slnx convention
            relativePath = relativePath.Replace('\\', '/');

            sb.AppendLine($"  <Project Path=\"{relativePath}\" />");
        }

        sb.AppendLine("</Solution>");

        File.WriteAllText(solutionFilePath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Determines if a reference appears to be a framework reference that comes
    /// with the .NET SDK and should not be included as an explicit reference.
    /// </summary>
    private static bool IsFrameworkReference(string filePath)
    {
        var normalizedPath = filePath.Replace('\\', '/');

        // Check for SDK packs directory (dotnet/packs)
        if (normalizedPath.Contains("/packs/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for shared framework directory
        if (normalizedPath.Contains("/shared/Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for Reference Assemblies (older .NET Framework style)
        if (normalizedPath.Contains("/Reference Assemblies/Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for NuGet packages that are framework references
        // These are typically restored from implicit framework references
        var packagesIndex = normalizedPath.IndexOf("/packages/", StringComparison.OrdinalIgnoreCase);
        if (packagesIndex >= 0)
        {
            var afterPackages = normalizedPath.Substring(packagesIndex + "/packages/".Length);
            // Framework packages from NuGet
            if (afterPackages.StartsWith("microsoft.netcore.app", StringComparison.OrdinalIgnoreCase) ||
                afterPackages.StartsWith("microsoft.aspnetcore.app", StringComparison.OrdinalIgnoreCase) ||
                afterPackages.StartsWith("microsoft.windowsdesktop.app", StringComparison.OrdinalIgnoreCase) ||
                afterPackages.StartsWith("netstandard.library", StringComparison.OrdinalIgnoreCase) ||
                afterPackages.StartsWith("microsoft.netframework.referenceassemblies", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines if a file is generated during the build process and should not be
    /// included in the exported project. Files under obj directories are typically
    /// generated by the build.
    /// </summary>
    private static bool IsGeneratedFile(string filePath)
    {
        var normalizedPath = filePath.Replace('\\', '/');
        return normalizedPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ExportedProjectInfo(
        string projectName,
        string exportedProjectFilePath,
        string projectDirectory,
        bool isCSharp,
        List<string> targetFrameworks,
        List<(CompilerCall Call, int Index)> compilerCalls)
    {
        public string ProjectName { get; } = projectName;
        public string ExportedProjectFilePath { get; } = exportedProjectFilePath;
        public string ProjectDirectory { get; } = projectDirectory;
        public bool IsCSharp { get; } = isCSharp;
        public List<string> TargetFrameworks { get; } = targetFrameworks;
        public List<(CompilerCall Call, int Index)> CompilerCalls { get; } = compilerCalls;
    }
}
