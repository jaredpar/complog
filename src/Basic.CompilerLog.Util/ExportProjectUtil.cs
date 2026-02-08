using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;

namespace Basic.CompilerLog.Util;

public sealed class ExportProjectUtil
{
    internal sealed class ProjectContentBuilder : PathNormalizationUtil
    {
        private PathNormalizationUtil PathNormalizationUtil { get; }
        internal string SourceDirectory { get; }
        internal string DestinationDirectory { get; }
        internal string SourceOutputDirectory { get; }
        internal string EmbeddedResourceDirectory { get; }
        internal ResilientDirectory AnalyzerDirectory { get; }
        internal ResilientDirectory GeneratedCodeDirectory { get; }
        private MiscDirectory MiscDirectory { get; }

        internal ProjectContentBuilder(string destinationDirectory, string originalSourceDirectory, PathNormalizationUtil pathNormalizationUtil)
        {
            PathNormalizationUtil = pathNormalizationUtil;
            DestinationDirectory = destinationDirectory;
            SourceDirectory = originalSourceDirectory;
            SourceOutputDirectory = Path.Combine(destinationDirectory, "src");
            EmbeddedResourceDirectory = Path.Combine(SourceOutputDirectory, "resources");
            AnalyzerDirectory = new(Path.Combine(SourceOutputDirectory, "analyzers"));
            GeneratedCodeDirectory = new(Path.Combine(SourceOutputDirectory, "generated"));
            MiscDirectory = new(Path.Combine(SourceOutputDirectory, "misc"));
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

            var normalizedPath = PathNormalizationUtil.NormalizePath(path);
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

            return MiscDirectory.GetNewFilePath(normalizedFullPath);
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

    private readonly record struct CompilerCallKey(string ProjectFilePath, string? TargetFramework, CompilerCallKind Kind, bool IsCSharp)
    {
        internal static CompilerCallKey Create(CompilerCall call) =>
            new(call.ProjectFilePath, call.TargetFramework, call.Kind, call.IsCSharp);
    }

    private sealed class ProjectItemCollection
    {
        internal HashSet<string> CompileItems { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> AdditionalFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> EditorConfigFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Analyzers { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<ResourceItem> Resources { get; } = new();
        internal HashSet<ReferenceItem> References { get; } = new();
        internal HashSet<string> ProjectReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ReferenceLayout
    {
        private readonly Dictionary<string, HashSet<Guid>> _referenceMap;

        internal ReferenceLayout(Dictionary<string, HashSet<Guid>> referenceMap)
        {
            _referenceMap = referenceMap;
        }

        internal string GetReferencePath(ReferenceData referenceData)
        {
            var fileName = referenceData.FileName;
            var mvids = _referenceMap[fileName];
            var basePath = Path.Combine("references", fileName);
            if (mvids.Count == 1)
            {
                return basePath;
            }

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            return Path.Combine("references", nameWithoutExtension, referenceData.Mvid.ToString(), fileName);
        }
    }

    private readonly record struct ReferenceItem(string Path, string Include, IReadOnlyList<string> Aliases, bool EmbedInteropTypes);

    private readonly record struct ResourceItem(string Path, string LogicalName);

    public CompilerLogReader Reader { get; }
    public bool ExcludeAnalyzers { get; }
    internal PathNormalizationUtil PathNormalizationUtil => Reader.PathNormalizationUtil;

    public ExportProjectUtil(CompilerLogReader reader, bool excludeAnalyzers = true)
    {
        Reader = reader;
        ExcludeAnalyzers = excludeAnalyzers;
    }

    public void ExportProject(IReadOnlyList<CompilerCall> compilerCalls, string destinationDir)
    {
        if (!Path.IsPathRooted(destinationDir))
        {
            throw new ArgumentException("Need a full path", nameof(destinationDir));
        }

        var compilerCallKeys = new HashSet<CompilerCallKey>(compilerCalls.Select(CompilerCallKey.Create));
        var compilerCallIndexMap = new Dictionary<CompilerCallKey, int>();
        for (int i = 0; i < Reader.Count; i++)
        {
            var call = Reader.ReadCompilerCall(i);
            var key = CompilerCallKey.Create(call);
            if (compilerCallKeys.Contains(key))
            {
                compilerCallIndexMap[key] = i;
            }
        }
        var compilerCallIndexToProjectFile = compilerCallIndexMap.ToDictionary(pair => pair.Value, pair => pair.Key.ProjectFilePath);

        var projectGroups = compilerCalls
            .GroupBy(call => call.ProjectFilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var projectPathMap = CreateProjectPathMap(projectGroups);
        var projectReferencesDir = Path.Combine(destinationDir, "references");
        var sourceRoot = Path.Combine(destinationDir, "src");
        _ = Directory.CreateDirectory(projectReferencesDir);
        _ = Directory.CreateDirectory(sourceRoot);

        var referenceLayout = BuildReferenceLayout(compilerCalls);
        var projectItems = projectGroups.ToDictionary(group => group.Key, _ => new ProjectItemCollection(), StringComparer.OrdinalIgnoreCase);
        WriteReferences(compilerCalls, referenceLayout, projectReferencesDir);

        try
        {
            foreach (var group in projectGroups)
            {
                var representativeCall = group.First();
                var builder = new ProjectContentBuilder(destinationDir, GetSourceDirectory(Reader, representativeCall), PathNormalizationUtil);
                Reader.PathNormalizationUtil = builder;

                foreach (var compilerCall in group)
                {
                    WriteContent(compilerCall, builder);
                    CollectProjectItems(compilerCall, builder, projectItems[group.Key], projectPathMap, compilerCallIndexToProjectFile, referenceLayout, destinationDir);
                }

                if (!ExcludeAnalyzers)
                {
                    WriteAnalyzers(group, builder, projectItems[group.Key], projectPathMap, destinationDir);
                }
            }
        }
        finally
        {
            Reader.PathNormalizationUtil = Reader.DefaultPathNormalizationUtil;
        }

        WriteProjectFiles(projectGroups, projectItems, projectPathMap, destinationDir);
        WriteSolutionFile(projectPathMap, destinationDir);
    }

    private static Dictionary<string, string> CreateProjectPathMap(IEnumerable<IGrouping<string, CompilerCall>> projectGroups)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in projectGroups)
        {
            var fileName = Path.GetFileName(group.Key);
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var projectDirName = baseName;
            var suffix = 1;
            while (!usedNames.Add(projectDirName))
            {
                projectDirName = $"{baseName}-{suffix}";
                suffix++;
            }

            var relativePath = Path.Combine("src", projectDirName, fileName);
            map[group.Key] = relativePath;
        }

        return map;
    }

    private ReferenceLayout BuildReferenceLayout(IEnumerable<CompilerCall> compilerCalls)
    {
        var referenceMap = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var compilerCall in compilerCalls)
        {
            foreach (var referenceData in Reader.ReadAllReferenceData(compilerCall))
            {
                if (IsFrameworkReference(referenceData))
                {
                    continue;
                }

                if (Reader.TryGetCompilerCallIndex(referenceData.Mvid, out _))
                {
                    continue;
                }

                if (!referenceMap.TryGetValue(referenceData.FileName, out var mvids))
                {
                    mvids = new HashSet<Guid>();
                    referenceMap[referenceData.FileName] = mvids;
                }

                mvids.Add(referenceData.Mvid);
            }
        }

        return new ReferenceLayout(referenceMap);
    }

    private void WriteReferences(IEnumerable<CompilerCall> compilerCalls, ReferenceLayout referenceLayout, string referencesDir)
    {
        foreach (var compilerCall in compilerCalls)
        {
            foreach (var referenceData in Reader.ReadAllReferenceData(compilerCall))
            {
                if (IsFrameworkReference(referenceData))
                {
                    continue;
                }

                if (Reader.TryGetCompilerCallIndex(referenceData.Mvid, out _))
                {
                    continue;
                }

                var relativePath = referenceLayout.GetReferencePath(referenceData);
                var relativeFilePath = PathUtil.RemovePathStart(relativePath, "references");
                var filePath = Path.Combine(referencesDir, relativeFilePath);
                if (File.Exists(filePath))
                {
                    continue;
                }

                _ = Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                Reader.CopyAssemblyBytes(referenceData.AssemblyData, fileStream);
            }
        }
    }

    private static bool IsFrameworkReference(ReferenceData referenceData)
    {
        var path = referenceData.FilePath;
        return path.Contains($"{Path.DirectorySeparatorChar}packs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Reference Assemblies", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Microsoft.NETCore.App.Ref", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteProjectFiles(
        IEnumerable<IGrouping<string, CompilerCall>> projectGroups,
        IReadOnlyDictionary<string, ProjectItemCollection> projectItems,
        IReadOnlyDictionary<string, string> projectPathMap,
        string destinationDir)
    {
        foreach (var group in projectGroups)
        {
            var projectFilePath = Path.Combine(destinationDir, projectPathMap[group.Key]);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(projectFilePath)!);

            var targetFrameworks = group
                .Select(call => call.TargetFramework)
                .Where(framework => !string.IsNullOrEmpty(framework))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var items = projectItems[group.Key];
            var project = new XElement("Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"));

            var propertyGroup = new XElement("PropertyGroup");
            if (targetFrameworks.Count == 1)
            {
                propertyGroup.Add(new XElement("TargetFramework", targetFrameworks[0]));
            }
            else if (targetFrameworks.Count > 1)
            {
                propertyGroup.Add(new XElement("TargetFrameworks", string.Join(";", targetFrameworks)));
            }

            if (propertyGroup.HasElements)
            {
                project.Add(propertyGroup);
            }

            AddItemGroup(project, "Compile", items.CompileItems);
            AddItemGroup(project, "AdditionalFiles", items.AdditionalFiles);
            AddItemGroup(project, "EditorConfigFiles", items.EditorConfigFiles);
            AddAnalyzerGroup(project, items.Analyzers);
            AddResourceGroup(project, items.Resources);
            AddReferenceGroup(project, items.References);
            AddProjectReferenceGroup(project, items.ProjectReferences);

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), project);
            doc.Save(projectFilePath);
        }
    }

    private static void WriteSolutionFile(IReadOnlyDictionary<string, string> projectPathMap, string destinationDir)
    {
        var solution = new XElement("Solution",
            projectPathMap.Values
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new XElement("Project", new XAttribute("Path", path))));
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), solution);
        doc.Save(Path.Combine(destinationDir, "export.slnx"));
    }

    private static void AddItemGroup(XElement project, string itemName, IEnumerable<string> items)
    {
        var itemList = items.ToList();
        Debug.Assert(itemList.Count == itemList.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Unexpected duplicate items.");
        if (itemList.Count == 0)
        {
            return;
        }

        var itemGroup = new XElement("ItemGroup");
        foreach (var item in itemList.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            itemGroup.Add(new XElement(itemName, new XAttribute("Include", item)));
        }

        project.Add(itemGroup);
    }

    private static void AddAnalyzerGroup(XElement project, IEnumerable<string> analyzers)
    {
        var analyzerList = analyzers.ToList();
        Debug.Assert(analyzerList.Count == analyzerList.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Unexpected duplicate analyzers.");
        if (analyzerList.Count == 0)
        {
            return;
        }

        var itemGroup = new XElement("ItemGroup");
        foreach (var analyzer in analyzerList.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            itemGroup.Add(new XElement("Analyzer", new XAttribute("Include", analyzer)));
        }

        project.Add(itemGroup);
    }

    private static void AddResourceGroup(XElement project, IEnumerable<ResourceItem> resources)
    {
        var resourceList = resources.ToList();
        Debug.Assert(resourceList.Count == resourceList.GroupBy(resource => resource.Path, StringComparer.OrdinalIgnoreCase).Count(), "Unexpected duplicate resources.");
        if (resourceList.Count == 0)
        {
            return;
        }

        var itemGroup = new XElement("ItemGroup");
        foreach (var resource in resourceList.OrderBy(resource => resource.Path, StringComparer.OrdinalIgnoreCase))
        {
            var element = new XElement("EmbeddedResource", new XAttribute("Include", resource.Path));
            element.Add(new XElement("LogicalName", resource.LogicalName));
            itemGroup.Add(element);
        }

        project.Add(itemGroup);
    }

    private static void AddReferenceGroup(XElement project, IEnumerable<ReferenceItem> references)
    {
        var referenceList = references.ToList();
        Debug.Assert(referenceList.Count == referenceList.GroupBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase).Count(), "Unexpected duplicate references.");
        if (referenceList.Count == 0)
        {
            return;
        }

        var itemGroup = new XElement("ItemGroup");
        foreach (var reference in referenceList.OrderBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase))
        {
            var element = new XElement("Reference", new XAttribute("Include", reference.Include));
            element.Add(new XElement("HintPath", reference.Path));
            if (reference.Aliases.Count > 0)
            {
                element.Add(new XElement("Aliases", string.Join(",", reference.Aliases)));
            }

            if (reference.EmbedInteropTypes)
            {
                element.Add(new XElement("EmbedInteropTypes", "true"));
            }

            itemGroup.Add(element);
        }

        project.Add(itemGroup);
    }

    private static void AddProjectReferenceGroup(XElement project, IEnumerable<string> references)
    {
        var referenceList = references.ToList();
        Debug.Assert(referenceList.Count == referenceList.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Unexpected duplicate project references.");
        if (referenceList.Count == 0)
        {
            return;
        }

        var itemGroup = new XElement("ItemGroup");
        foreach (var reference in referenceList.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            itemGroup.Add(new XElement("ProjectReference", new XAttribute("Include", reference)));
        }

        project.Add(itemGroup);
    }

    private void WriteContent(CompilerCall compilerCall, ProjectContentBuilder builder)
    {
        foreach (var rawContent in Reader.ReadAllRawContent(compilerCall))
        {
            if (rawContent.ContentHash is null)
            {
                continue;
            }

            var filePath = Reader.PathNormalizationUtil.NormalizePath(rawContent.FilePath, rawContent.Kind);
            if (filePath is null || File.Exists(filePath))
            {
                continue;
            }

            if (!Path.IsPathRooted(filePath))
            {
                filePath = Path.Combine(builder.DestinationDirectory, filePath);
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using var contentStream = Reader.GetContentStream(rawContent.Kind, rawContent.ContentHash);
            contentStream.WriteTo(filePath);
        }

        foreach (var resourceData in Reader.ReadAllResourceData(compilerCall))
        {
            var description = Reader.ReadResourceDescription(resourceData);
            var originalFileName = description.GetFileName();
            var resourceName = description.GetResourceName();
            var fileName = originalFileName ?? resourceName;
            var filePath = Path.Combine(builder.EmbeddedResourceDirectory, resourceData.ContentHash, fileName);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            if (!File.Exists(filePath))
            {
                File.WriteAllBytes(filePath, Reader.GetContentBytes(resourceData));
            }
        }
    }

    private void CollectProjectItems(
        CompilerCall compilerCall,
        ProjectContentBuilder builder,
        ProjectItemCollection items,
        IReadOnlyDictionary<string, string> projectPathMap,
        IReadOnlyDictionary<int, string> compilerCallIndexToProjectFile,
        ReferenceLayout referenceLayout,
        string destinationDir)
    {
        foreach (var sourceTextData in Reader.ReadAllSourceTextData(compilerCall))
        {
            var relativePath = GetProjectRelativePath(sourceTextData.FilePath, compilerCall, projectPathMap, destinationDir);
            switch (sourceTextData.SourceTextKind)
            {
                case SourceTextKind.SourceCode:
                    if (!IsUnderProjectDirectory(sourceTextData.FilePath, compilerCall, projectPathMap, destinationDir))
                    {
                        items.CompileItems.Add(relativePath);
                    }
                    break;
                case SourceTextKind.AdditionalText:
                    items.AdditionalFiles.Add(relativePath);
                    break;
                case SourceTextKind.AnalyzerConfig:
                    items.EditorConfigFiles.Add(relativePath);
                    break;
            }
        }

        if (ExcludeAnalyzers)
        {
            foreach (var rawContent in Reader.ReadAllRawContent(compilerCall, RawContentKind.GeneratedText))
            {
                var filePath = Reader.PathNormalizationUtil.NormalizePath(rawContent.FilePath, rawContent.Kind);
                if (filePath is null)
                {
                    continue;
                }

                var relativePath = GetProjectRelativePath(filePath, compilerCall, projectPathMap, destinationDir);
                items.CompileItems.Add(relativePath);
            }
        }

        foreach (var resourceData in Reader.ReadAllResourceData(compilerCall))
        {
            var description = Reader.ReadResourceDescription(resourceData);
            var originalFileName = description.GetFileName();
            var resourceName = description.GetResourceName();
            var fileName = originalFileName ?? resourceName;
            var filePath = Path.Combine(builder.EmbeddedResourceDirectory, resourceData.ContentHash, fileName);
            var relativePath = GetProjectRelativePath(filePath, compilerCall, projectPathMap, destinationDir);
            items.Resources.Add(new ResourceItem(relativePath, resourceName));
        }

        var seenReferences = new HashSet<Guid>();
        foreach (var referenceData in Reader.ReadAllReferenceData(compilerCall))
        {
            if (!seenReferences.Add(referenceData.Mvid))
            {
                continue;
            }

            if (Reader.TryGetCompilerCallIndex(referenceData.Mvid, out var refCompilerCallIndex)
                && compilerCallIndexToProjectFile.TryGetValue(refCompilerCallIndex, out var refProjectFile)
                && projectPathMap.TryGetValue(refProjectFile, out var refProjectPath))
            {
                items.ProjectReferences.Add(GetProjectRelativePath(Path.Combine(destinationDir, refProjectPath), compilerCall, projectPathMap, destinationDir));
                continue;
            }

            if (IsFrameworkReference(referenceData))
            {
                continue;
            }

            var referencePath = referenceLayout.GetReferencePath(referenceData);
            items.References.Add(new ReferenceItem(
                GetProjectRelativePath(Path.Combine(destinationDir, referencePath), compilerCall, projectPathMap, destinationDir),
                referenceData.AssemblyIdentityData.AssemblyName ?? Path.GetFileNameWithoutExtension(referenceData.FileName),
                referenceData.Aliases,
                referenceData.EmbedInteropTypes));
        }
    }

    private void WriteAnalyzers(
        IEnumerable<CompilerCall> group,
        ProjectContentBuilder builder,
        ProjectItemCollection items,
        IReadOnlyDictionary<string, string> projectPathMap,
        string destinationDir)
    {
        foreach (var compilerCall in group)
        {
            foreach (var analyzer in Reader.ReadAllAnalyzerData(compilerCall))
            {
                var filePath = builder.AnalyzerDirectory.GetNewFilePath(analyzer.FilePath);
                if (!Path.IsPathRooted(filePath))
                {
                    filePath = Path.Combine(builder.DestinationDirectory, filePath);
                }
                if (!File.Exists(filePath))
                {
                    using var analyzerStream = Reader.GetAssemblyStream(analyzer.Mvid);
                    analyzerStream.WriteTo(filePath);
                }

                items.Analyzers.Add(GetProjectRelativePath(filePath, compilerCall, projectPathMap, destinationDir));
            }
        }
    }

    private static string GetProjectRelativePath(
        string filePath,
        CompilerCall compilerCall,
        IReadOnlyDictionary<string, string> projectPathMap,
        string destinationDir)
    {
        if (!Path.IsPathRooted(filePath))
        {
            filePath = Path.Combine(destinationDir, filePath);
        }

        var projectDir = GetProjectDirectoryPath(compilerCall, projectPathMap, destinationDir);
        return GetRelativePath(projectDir, filePath);
    }

    private static string GetProjectDirectoryPath(
        CompilerCall compilerCall,
        IReadOnlyDictionary<string, string> projectPathMap,
        string destinationDir)
    {
        var projectPath = projectPathMap[compilerCall.ProjectFilePath];
        return Path.Combine(destinationDir, Path.GetDirectoryName(projectPath)!);
    }

    private static bool IsUnderProjectDirectory(
        string filePath,
        CompilerCall compilerCall,
        IReadOnlyDictionary<string, string> projectPathMap,
        string destinationDir)
    {
        if (!Path.IsPathRooted(filePath))
        {
            filePath = Path.Combine(destinationDir, filePath);
        }

        var projectDir = GetProjectDirectoryPath(compilerCall, projectPathMap, destinationDir);
        return filePath.StartsWith(projectDir, PathUtil.Comparison);
    }

    private static string GetRelativePath(string basePath, string path)
    {
        if (path.StartsWith(basePath, PathUtil.Comparison))
        {
            return PathUtil.RemovePathStart(path, basePath);
        }

        if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), PathUtil.Comparison))
        {
            basePath += Path.DirectorySeparatorChar;
        }

        var baseUri = new Uri(basePath);
        var pathUri = new Uri(path);
        var relativeUri = baseUri.MakeRelativeUri(pathUri);
        return Uri.UnescapeDataString(relativeUri.ToString())
            .Replace('/', Path.DirectorySeparatorChar);
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
