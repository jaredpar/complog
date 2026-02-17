using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Basic.CompilerLog.Util;

public sealed partial class ExportUtil
{
    internal sealed class ContentBuilder : PathNormalizationUtil
    {
        /// <summary>
        /// This is the <see cref="PathNormalizationUtil"/> that was used by the <see cref="CompilerLogReader"/>.
        /// </summary>
        private PathNormalizationUtil PathNormalizationUtil { get; }

        /// <summary>
        /// This is the root most directory where the compilation occurred. This can be more root than <see cref="ProjectDirectory"/> 
        /// when there are .editorconfig files that are above the project directory. This path is after going through
        /// <see cref="PathNormalizationUtil.NormalizePath(string?)"/>.
        /// </summary>
        internal string SourceDirectory { get; }

        /// <summary>
        /// This is the original project directory where the compilation occurred. This is after it went through
        /// <see cref="PathNormalizationUtil.NormalizePath(string?)"/>.
        /// </summary>
        internal string ProjectDirectory { get; }

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

        internal ContentBuilder(string destinationDirectory, string originalSourceDirectory, string projectDirectory, PathNormalizationUtil pathNormalizationUtil)
        {
            PathNormalizationUtil = pathNormalizationUtil;
            DestinationDirectory = destinationDirectory;
            SourceDirectory = originalSourceDirectory;
            ProjectDirectory = projectDirectory;
            SourceOutputDirectory = Path.Combine(destinationDirectory, "src");
            EmbeddedResourceDirectory = Path.Combine(destinationDirectory, "resources");
            MiscDirectory = new(Path.Combine(destinationDirectory, "misc"));
            GeneratedCodeDirectory = new(Path.Combine(destinationDirectory, "generated"));
            AnalyzerDirectory = new(Path.Combine(destinationDirectory, "analyzers"));
            BuildOutput = new(Path.Combine(destinationDirectory, "output"), flatten: true);
            _ = Directory.CreateDirectory(SourceOutputDirectory);
            _ = Directory.CreateDirectory(EmbeddedResourceDirectory);
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

            // If the path isn't rooted then it was relative to the project directory when it was recorded.
            var normalizedFullPath = Path.IsPathRooted(normalizedPath)
                ? normalizedPath
                : Path.Combine(ProjectDirectory, normalizedPath);

            // Try and remove any relative elements from the paths to make it a bit cleaner.
            normalizedFullPath = Path.GetFullPath(normalizedFullPath);

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
    }

    public CompilerLogReader Reader { get; }
    public bool ExcludeAnalyzers { get; }
    internal PathNormalizationUtil PathNormalizationUtil => Reader.PathNormalizationUtil;

    public ExportUtil(CompilerLogReader reader, bool excludeAnalyzers = true)
    {
        Reader = reader;
        ExcludeAnalyzers = excludeAnalyzers;
    }

    internal void ExportAll(
        string destinationDir,
        IReadOnlyList<(string CompilerDirectory, string Name)> compilerDirectories,
        Func<CompilerCall, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        for (int  i = 0; i < Reader.Count ; i++)
        {
            var compilerCall = Reader.ReadCompilerCall(i);
            if (predicate(compilerCall))
            {
                var dir = Path.Combine(destinationDir, i.ToString());
                Directory.CreateDirectory(dir);
                Export(compilerCall, dir, compilerDirectories);
            }
        }
    }

    public void Export(
        CompilerCall compilerCall,
        string destinationDir,
        IReadOnlyList<(string CompilerDirectory, string Name)> compilerDirectories)
    {
        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        var originalSourceDirectory = GetSourceDirectory(Reader, compilerCall);
        var builder = new ContentBuilder(
            destinationDir,
            originalSourceDirectory,
            PathNormalizationUtil.NormalizePath(compilerCall.ProjectDirectory),
            PathNormalizationUtil);
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

            foreach (var compiler in compilerDirectories)
            {
                var cmdFileName = $"build-{MakeSafeFileName(compiler.Name)}";
                WriteBuildCmd(compiler.CompilerDirectory, cmdFileName);
            }

            WriteBuildCmd(compilerDirectories[0].CompilerDirectory, "build");

        }
        finally
        {
            Reader.PathNormalizationUtil = Reader.DefaultPathNormalizationUtil;
        }

        static string MakeSafeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                builder.Append(invalidChars.Contains(ch) ? '_' : ch);
            }

            return builder.ToString();
        }

        void WriteBuildCmd(string compilerDirectory, string cmdFileName)
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

            var noConfig = hasNoConfigOption
                ? "/noconfig "
                : string.Empty;

            var compilerCommand = RoslynUtil.GetCompilerInvocation(compilerDirectory, compilerCall.IsCSharp);
            lines.Add($@"{compilerCommand} {noConfig}@build.rsp");
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
    /// Export compilations as a full solution with project files that can be opened in VS or VS Code
    /// </summary>
    public void ExportSolution(string destinationDir, Func<CompilerCall, bool>? predicate = null)
    {
        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        predicate ??= static _ => true;

        _ = Directory.CreateDirectory(destinationDir);

        var referencesDir = Path.Combine(destinationDir, "references");
        _ = Directory.CreateDirectory(referencesDir);

        var projectInfos = new List<(int Index, CompilerCall CompilerCall, string ProjectDir, string ProjectFileName)>();
        var mvidToReferenceFile = new Dictionary<Guid, string>();

        // First pass: collect all projects and write DLLs
        for (int i = 0; i < Reader.Count; i++)
        {
            var compilerCall = Reader.ReadCompilerCall(i);
            if (!predicate(compilerCall))
            {
                continue;
            }

            if (compilerCall.Kind != CompilerCallKind.Regular)
            {
                continue;
            }

            var projectName = GetProjectName(compilerCall, i);
            var projectDir = Path.Combine(destinationDir, projectName);
            var projectFileName = $"{projectName}.csproj";
            if (compilerCall.IsVisualBasic)
            {
                projectFileName = $"{projectName}.vbproj";
            }

            projectInfos.Add((i, compilerCall, projectDir, projectFileName));
        }

        var refMvidToFilePathMap = WriteReferencesDirectory(projectInfos.Select(p => p.CompilerCall));

        // Second pass: create project files
        foreach (var (index, compilerCall, projectDir, projectFileName) in projectInfos)
        {
            Directory.CreateDirectory(projectDir);

            var projectFilePath = Path.Combine(projectDir, projectFileName);
            CreateProjectFile(index, compilerCall, projectFilePath, projectInfos, refMvidToFilePathMap);
        }

        // Create solution file
        var solutionFilePath = Path.Combine(destinationDir, "export.slnx");
        CreateSolutionFile(solutionFilePath, projectInfos, destinationDir);

        Dictionary<Guid, string> WriteReferencesDirectory(IEnumerable<CompilerCall> compilerCalls)
        {
            var refList = new List<ReferenceData>();

            // This tracks whether or not a name has been seen with multiple MVIDs. If so then we need to
            // disambiguate by putting the files in separate folders by MVID. If not then we can just put
            // the file directly in the references folder.
            var nameMap = new Dictionary<string, bool>();
            foreach (var compilerCall in compilerCalls)
            {
                foreach (var referenceData in Reader.ReadAllReferenceData(compilerCall))
                {
                    if (Reader.TryGetCompilerCallIndex(referenceData.Mvid, out _))
                    {
                        continue;
                    }

                    if (IsFrameworkReference(referenceData.FilePath, compilerCall.TargetFramework))
                    {
                        continue;
                    }

                    refList.Add(referenceData);

                    var fileName = referenceData.FileName;
                    if (!nameMap.ContainsKey(fileName))
                    {
                        nameMap[fileName] = false;
                    }
                    else
                    {
                        nameMap[fileName] = true;
                    }
                }
            }

            var map = new Dictionary<Guid, string>();
            foreach (var refData in refList)
            {
                var fileName = refData.FileName;
                string relativePath;
                if (!nameMap[fileName])
                {
                    relativePath = fileName;
                }
                else
                {
                    relativePath = Path.Combine(fileName, refData.Mvid.ToString("N"));
                    _ = Directory.CreateDirectory(Path.Combine(referencesDir, relativePath));
                    relativePath = Path.Combine(relativePath, fileName);
                }

                var filePath = Path.Combine(referencesDir, relativePath);
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                Reader.CopyAssemblyBytes(refData.Mvid, fileStream);
                fileStream.Close();

                map[refData.Mvid] = relativePath;
            }

            return map;
        }
    }

    private string GetProjectName(CompilerCall compilerCall, int index)
    {
        var baseName = Path.GetFileNameWithoutExtension(compilerCall.ProjectFileName);
        if (!string.IsNullOrEmpty(compilerCall.TargetFramework))
        {
            return $"{baseName}-{compilerCall.TargetFramework}";
        }
        return baseName;
    }

    private void CreateProjectFile(
        int index,
        CompilerCall compilerCall,
        string projectFilePath,
        List<(int Index, CompilerCall CompilerCall, string ProjectDir, string ProjectFileName)> allProjects,
        Dictionary<Guid, string> refMvidToFilePathMap)
    {
        var compilerCallData = Reader.ReadCompilerCallData(compilerCall);
        var sb = new StringBuilder();

        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");

        // Determine TargetFramework or TargetFrameworks
        var hasTargetFramework = !string.IsNullOrEmpty(compilerCall.TargetFramework);
        if (hasTargetFramework)
        {
            sb.AppendLine($"    <TargetFramework>{compilerCall.TargetFramework}</TargetFramework>");
        }
        else
        {
            // No target framework specified - use a default and disable standard lib
            sb.AppendLine("    <TargetFramework>net9.0</TargetFramework>");
            sb.AppendLine("    <NoStdLib>true</NoStdLib>");
        }

        // Add assembly name
        var assemblyName = Path.GetFileNameWithoutExtension(compilerCallData.AssemblyFileName);
        sb.AppendLine($"    <AssemblyName>{assemblyName}</AssemblyName>");

        // For Visual Basic projects, set RootNamespace to avoid issues with hyphens in project name
        if (compilerCall.IsVisualBasic)
        {
            sb.AppendLine($"    <RootNamespace>{assemblyName.Replace('-', '_')}</RootNamespace>");
        }

        // Determine output type
        if (compilerCallData.CompilationOptions.OutputKind == Microsoft.CodeAnalysis.OutputKind.ConsoleApplication ||
            compilerCallData.CompilationOptions.OutputKind == Microsoft.CodeAnalysis.OutputKind.WindowsApplication ||
            compilerCallData.CompilationOptions.OutputKind == Microsoft.CodeAnalysis.OutputKind.WindowsRuntimeApplication)
        {
            sb.AppendLine("    <OutputType>Exe</OutputType>");
        }
        else if (compilerCallData.CompilationOptions.OutputKind == Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary)
        {
            sb.AppendLine("    <OutputType>Library</OutputType>");
        }

        var allReferences = Reader.ReadAllReferenceData(compilerCall);
        if (UsesWpf(allReferences))
        {
            sb.AppendLine("    <UseWPF>true</UseWPF>");
        }
        else if (UsesWinForms(allReferences))
        {
            sb.AppendLine("    <UseWindowsForms>true</UseWindowsForms>");
        }


        // Disable auto-generation of assembly info since we're including the original
        sb.AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        sb.AppendLine("    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>");

        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();

        // Add references
        var projectReferences = new List<(string Path, ImmutableArray<string> Aliases)>();
        var metadataReferences = new List<(string Path, ImmutableArray<string> Aliases)>();

        foreach (var refData in Reader.ReadAllReferenceData(compilerCall))
        {
            if (Reader.TryGetCompilerCallIndex(refData.Mvid, out var refIndex))
            {
                // This is a project reference
                var refProject = allProjects.FirstOrDefault(p => p.Index == refIndex);
                if (refProject != default)
                {
                    var relativePath = Path.GetRelativePath(Path.GetDirectoryName(projectFilePath)!, Path.Combine(refProject.ProjectDir, refProject.ProjectFileName));
                    projectReferences.Add((relativePath, refData.Aliases));
                }
            }
            else if (!IsFrameworkReference(refData.FilePath, compilerCall.TargetFramework))
            {
                // This is an external reference (not a framework reference)
                var refFileName = refMvidToFilePathMap[refData.Mvid];
                var refPath = Path.Combine("..", "references", refFileName);
                metadataReferences.Add((refPath, refData.Aliases));
            }
        }

        if (projectReferences.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var (refPath, aliases) in projectReferences)
            {
                if (aliases.Length > 0)
                {
                    sb.AppendLine($"    <ProjectReference Include=\"{refPath}\">");
                    sb.AppendLine($"      <Aliases>{string.Join(",", aliases)}</Aliases>");
                    sb.AppendLine("    </ProjectReference>");
                }
                else
                {
                    sb.AppendLine($"    <ProjectReference Include=\"{refPath}\" />");
                }
            }
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
        }

        if (metadataReferences.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var (refPath, aliases) in metadataReferences)
            {
                var refFileName = Path.GetFileName(refPath);
                var refNameWithoutExt = Path.GetFileNameWithoutExtension(refFileName);
                if (aliases.Length > 0)
                {
                    sb.AppendLine($"    <Reference Include=\"{refNameWithoutExt}\">");
                    sb.AppendLine($"      <HintPath>{refPath}</HintPath>");
                    sb.AppendLine($"      <Aliases>{string.Join(",", aliases)}</Aliases>");
                    sb.AppendLine("    </Reference>");
                }
                else
                {
                    sb.AppendLine($"    <Reference Include=\"{refNameWithoutExt}\">");
                    sb.AppendLine($"      <HintPath>{refPath}</HintPath>");
                    sb.AppendLine("    </Reference>");
                }
            }
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
        }

        sb.AppendLine("</Project>");

        File.WriteAllText(projectFilePath, sb.ToString());

        // Copy source files to project directory maintaining their relative paths
        var projectDir = Path.GetDirectoryName(projectFilePath)!;
        var originalProjectDir = Path.GetDirectoryName(compilerCall.ProjectFilePath)!;
        var binStr = $"bin{Path.DirectorySeparatorChar}";
        var objStr = $"obj{Path.DirectorySeparatorChar}";
        foreach (var sourceFile in Reader.ReadAllSourceTextData(compilerCall))
        {
            string destPath;
            var normalizedSourcePath = Path.GetFullPath(sourceFile.FilePath);
            if (normalizedSourcePath.StartsWith(originalProjectDir, PathUtil.Comparison))
            {
                var relativePath = Path.GetRelativePath(originalProjectDir, normalizedSourcePath);
                if (relativePath.StartsWith(binStr, StringComparison.Ordinal) || relativePath.StartsWith(objStr, StringComparison.Ordinal))
                {
                    destPath = Path.Combine(projectDir, "external", Path.GetFileName(sourceFile.FilePath));
                }
                else
                {
                    destPath = Path.Combine(projectDir, relativePath);
                }
            }
            else
            {
                destPath = Path.Combine(projectDir, "external", Path.GetFileName(sourceFile.FilePath));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            var sourceText = Reader.ReadSourceText(sourceFile);
            File.WriteAllText(destPath, sourceText.ToString());
        }

        static bool UsesWinForms(IEnumerable<ReferenceData> references)
        {
            var p = Path.Combine("packs", "Microsoft.WindowsDesktop.App.Ref");
            return references.Any(x => x.FilePath.Contains(p));
        }

        static bool UsesWpf(IEnumerable<ReferenceData> references)
        {
            var p = Path.Combine("packs", "Microsoft.WindowsDesktop.App.Ref");
            return references.Any(x => x.FilePath.Contains(p) && x.FileName == "PresentationCore.dll");
        }
    }

    internal static bool IsFrameworkReference(string filePath, string? targetFramework)
    {
        if (string.IsNullOrEmpty(targetFramework))
        {
            return false;
        }

        var sep = Path.DirectorySeparatorChar;
        if (filePath.Contains($"{sep}packs{sep}", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains($"{sep}shared{sep}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private void CreateSolutionFile(
        string solutionFilePath,
        List<(int Index, CompilerCall CompilerCall, string ProjectDir, string ProjectFileName)> projectInfos,
        string destinationDir)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<Solution>");
        foreach (var (_, _, projectDir, projectFileName) in projectInfos)
        {
            var relativePath = Path.GetRelativePath(destinationDir, Path.Combine(projectDir, projectFileName));
            // .slnx files are XML-based and use forward slashes regardless of platform
            // This ensures cross-platform compatibility of the solution file
            relativePath = relativePath.Replace('\\', '/');
            sb.AppendLine($"  <Project Path=\"{relativePath}\" />");
        }
        sb.AppendLine("</Solution>");

        File.WriteAllText(solutionFilePath, sb.ToString());
    }
}
