using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.VisualBasic;
using System.Collections.Immutable;

namespace Basic.CompilerLog.WorkspaceComparer;

/// <summary>
/// Describes a difference between two Roslyn solutions.
/// </summary>
internal sealed class WorkspaceDifference
{
    public string Path { get; }
    public string? Expected { get; }
    public string? Actual { get; }

    public WorkspaceDifference(string path, string? expected, string? actual)
    {
        Path = path;
        Expected = expected;
        Actual = actual;
    }

    public override string ToString() =>
        $"{Path}: expected {Format(Expected)}, actual {Format(Actual)}";

    private static string Format(string? value) => value is null ? "<missing>" : $"\"{value}\"";
}

/// <summary>
/// Compares the observable structure of two Roslyn solutions.
/// </summary>
internal static class WorkspaceComparer
{
    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static ImmutableArray<WorkspaceDifference> Compare(Solution expected, Solution actual)
    {
        var builder = ImmutableArray.CreateBuilder<WorkspaceDifference>();
        CompareValue(builder, "Solution.ProjectCount", expected.ProjectIds.Count, actual.ProjectIds.Count);

        var expectedProjects = GroupProjects(expected.Projects);
        var actualProjects = GroupProjects(actual.Projects);
        foreach (var filePath in expectedProjects.Keys.Union(actualProjects.Keys, PathComparer).OrderBy(x => x, PathComparer))
        {
            expectedProjects.TryGetValue(filePath, out var expectedGroup);
            actualProjects.TryGetValue(filePath, out var actualGroup);
            expectedGroup ??= [];
            actualGroup ??= [];

            var pairs = PairProjects(expectedGroup, actualGroup);
            foreach (var pair in pairs)
            {
                var projectPath = GetProjectPath(filePath, pair.Expected ?? pair.Actual!);
                if (pair.Expected is null)
                {
                    builder.Add(new(projectPath, expected: null, DescribeProject(pair.Actual!)));
                }
                else if (pair.Actual is null)
                {
                    builder.Add(new(projectPath, DescribeProject(pair.Expected), actual: null));
                }
                else
                {
                    CompareProject(builder, projectPath, pair.Expected, pair.Actual);
                }
            }
        }

        return builder.ToImmutable();
    }

    private static Dictionary<string, List<Project>> GroupProjects(IEnumerable<Project> projects)
    {
        var map = new Dictionary<string, List<Project>>(PathComparer);
        foreach (var project in projects)
        {
            var key = project.FilePath ?? $"<no file path>:{project.Name}:{project.Language}";
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map.Add(key, list);
            }

            list.Add(project);
        }

        return map;
    }

    private static List<(Project? Expected, Project? Actual)> PairProjects(List<Project> expected, List<Project> actual)
    {
        var result = new List<(Project?, Project?)>();
        var unmatchedActual = new List<Project>(actual);
        foreach (var expectedProject in expected.OrderBy(GetProjectSortKey, StringComparer.Ordinal))
        {
            var index = -1;
            var expectedTargetFramework = GetTargetFrameworkMoniker(expectedProject);
            if (expectedProject.OutputFilePath is { } expectedOutputFilePath &&
                expected.Count(project => PathComparer.Equals(expectedOutputFilePath, project.OutputFilePath)) == 1 &&
                actual.Count(project => PathComparer.Equals(expectedOutputFilePath, project.OutputFilePath)) == 1)
            {
                index = unmatchedActual.FindIndex(actualProject =>
                    PathComparer.Equals(expectedOutputFilePath, actualProject.OutputFilePath));
            }

            if (index < 0 && expectedTargetFramework is not null)
            {
                index = unmatchedActual.FindIndex(actualProject =>
                    string.Equals(expectedTargetFramework, GetTargetFrameworkMoniker(actualProject), StringComparison.Ordinal));
            }

            if (index < 0 &&
                unmatchedActual.Count > 0 &&
                (expectedTargetFramework is null || unmatchedActual.All(project => GetTargetFrameworkMoniker(project) is null)))
            {
                index = 0;
            }

            if (index < 0)
            {
                result.Add((expectedProject, null));
            }
            else
            {
                result.Add((expectedProject, unmatchedActual[index]));
                unmatchedActual.RemoveAt(index);
            }
        }

        result.AddRange(unmatchedActual
            .OrderBy(GetProjectSortKey, StringComparer.Ordinal)
            .Select(static project => ((Project?)null, (Project?)project)));
        return result;
    }

    private static void CompareProject(
        ImmutableArray<WorkspaceDifference>.Builder builder,
        string path,
        Project expected,
        Project actual)
    {
        CompareValue(builder, $"{path}.Name", expected.Name, actual.Name);
        CompareValue(builder, $"{path}.AssemblyName", expected.AssemblyName, actual.AssemblyName);
        CompareValue(builder, $"{path}.Language", expected.Language, actual.Language);
        CompareValue(builder, $"{path}.FilePath", expected.FilePath, actual.FilePath);
        CompareValue(builder, $"{path}.OutputFilePath", expected.OutputFilePath, actual.OutputFilePath);

        CompareDocuments(builder, path, "Documents", expected.Documents, actual.Documents);
        CompareDocuments(builder, path, "AdditionalDocuments", expected.AdditionalDocuments, actual.AdditionalDocuments);
        CompareDocuments(builder, path, "AnalyzerConfigDocuments", expected.AnalyzerConfigDocuments, actual.AnalyzerConfigDocuments);
        CompareProjectReferences(builder, path, expected, actual);
        CompareMetadataReferences(builder, path, expected.MetadataReferences, actual.MetadataReferences);
        CompareAnalyzerReferences(builder, path, expected.AnalyzerReferences, actual.AnalyzerReferences);
        CompareCompilationOptions(builder, path, expected.CompilationOptions, actual.CompilationOptions);
        CompareParseOptions(builder, path, expected.ParseOptions, actual.ParseOptions);
    }

    private static void CompareDocuments(
        ImmutableArray<WorkspaceDifference>.Builder builder,
        string projectPath,
        string collectionName,
        IEnumerable<TextDocument> expected,
        IEnumerable<TextDocument> actual)
    {
        var expectedDocuments = GroupByPath(expected);
        var actualDocuments = GroupByPath(actual);
        CompareValue(builder, $"{projectPath}.{collectionName}.Count", expectedDocuments.Sum(x => x.Value.Count), actualDocuments.Sum(x => x.Value.Count));

        foreach (var filePath in expectedDocuments.Keys.Union(actualDocuments.Keys, PathComparer).OrderBy(x => x, PathComparer))
        {
            expectedDocuments.TryGetValue(filePath, out var expectedGroup);
            actualDocuments.TryGetValue(filePath, out var actualGroup);
            expectedGroup ??= [];
            actualGroup ??= [];
            var count = Math.Max(expectedGroup.Count, actualGroup.Count);
            for (var i = 0; i < count; i++)
            {
                var expectedDocument = i < expectedGroup.Count ? expectedGroup[i] : null;
                var actualDocument = i < actualGroup.Count ? actualGroup[i] : null;
                var path = $"{projectPath}.{collectionName}[{filePath}]";
                if (expectedDocument is null)
                {
                    builder.Add(new(path, expected: null, DescribeDocument(actualDocument!)));
                    continue;
                }

                if (actualDocument is null)
                {
                    builder.Add(new(path, DescribeDocument(expectedDocument), actual: null));
                    continue;
                }

                CompareValue(builder, $"{path}.Name", expectedDocument.Name, actualDocument.Name);
                CompareValue(builder, $"{path}.FilePath", expectedDocument.FilePath, actualDocument.FilePath);
                CompareValue(builder, $"{path}.Folders", string.Join("/", expectedDocument.Folders), string.Join("/", actualDocument.Folders));
                if (expectedDocument is Document expectedSource && actualDocument is Document actualSource)
                {
                    CompareValue(builder, $"{path}.SourceCodeKind", expectedSource.SourceCodeKind, actualSource.SourceCodeKind);
                }
            }
        }

        static Dictionary<string, List<TextDocument>> GroupByPath(IEnumerable<TextDocument> documents)
        {
            var map = new Dictionary<string, List<TextDocument>>(PathComparer);
            foreach (var document in documents)
            {
                var key = document.FilePath ?? $"<no file path>:{document.Name}";
                if (!map.TryGetValue(key, out var list))
                {
                    list = [];
                    map.Add(key, list);
                }

                list.Add(document);
            }

            return map;
        }
    }

    private static void CompareProjectReferences(
        ImmutableArray<WorkspaceDifference>.Builder builder,
        string projectPath,
        Project expected,
        Project actual)
    {
        var expectedReferences = Group(expected);
        var actualReferences = Group(actual);
        CompareValue(builder, $"{projectPath}.ProjectReferences.Count", expected.ProjectReferences.Count(), actual.ProjectReferences.Count());

        foreach (var key in expectedReferences.Keys.Union(actualReferences.Keys, PathComparer).OrderBy(x => x, PathComparer))
        {
            expectedReferences.TryGetValue(key, out var expectedGroup);
            actualReferences.TryGetValue(key, out var actualGroup);
            expectedGroup ??= [];
            actualGroup ??= [];
            var count = Math.Max(expectedGroup.Count, actualGroup.Count);
            for (var i = 0; i < count; i++)
            {
                var expectedReference = i < expectedGroup.Count ? expectedGroup[i] : default;
                var actualReference = i < actualGroup.Count ? actualGroup[i] : default;
                var expectedTarget = expectedReference.Target is null ? null : DescribeProject(expectedReference.Target);
                var actualTarget = actualReference.Target is null ? null : DescribeProject(actualReference.Target);
                var path = $"{projectPath}.ProjectReferences[{key}{GetIndexSuffix(count, i)}]";
                if (expectedReference.Reference is null || actualReference.Reference is null)
                {
                    builder.Add(new(path, expectedTarget, actualTarget));
                    continue;
                }

                CompareValue(builder, $"{path}.Target", expectedTarget, actualTarget);
                CompareValue(builder, $"{path}.Aliases", FormatAliases(expectedReference.Reference.Aliases), FormatAliases(actualReference.Reference.Aliases));
                CompareValue(builder, $"{path}.EmbedInteropTypes", expectedReference.Reference.EmbedInteropTypes, actualReference.Reference.EmbedInteropTypes);
            }
        }

        static Dictionary<string, List<(ProjectReference Reference, Project? Target)>> Group(Project project) =>
            project.ProjectReferences
                .Select(reference => (Reference: reference, Target: project.Solution.GetProject(reference.ProjectId)))
                .GroupBy(static pair => GetProjectReferenceKey(pair.Target), PathComparer)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(pair => FormatAliases(pair.Reference.Aliases), StringComparer.Ordinal).ToList(),
                    PathComparer);
    }

    private static void CompareMetadataReferences(
        ImmutableArray<WorkspaceDifference>.Builder builder,
        string projectPath,
        IEnumerable<MetadataReference> expected,
        IEnumerable<MetadataReference> actual)
    {
        var expectedReferences = expected.GroupBy(GetReferenceName, PathComparer).ToDictionary(x => x.Key, x => x.OrderBy(GetReferenceSortKey, PathComparer).ToList(), PathComparer);
        var actualReferences = actual.GroupBy(GetReferenceName, PathComparer).ToDictionary(x => x.Key, x => x.OrderBy(GetReferenceSortKey, PathComparer).ToList(), PathComparer);
        CompareValue(builder, $"{projectPath}.MetadataReferences.Count", expectedReferences.Sum(x => x.Value.Count), actualReferences.Sum(x => x.Value.Count));

        foreach (var name in expectedReferences.Keys.Union(actualReferences.Keys, PathComparer).OrderBy(x => x, PathComparer))
        {
            expectedReferences.TryGetValue(name, out var expectedGroup);
            actualReferences.TryGetValue(name, out var actualGroup);
            expectedGroup ??= [];
            actualGroup ??= [];
            var count = Math.Max(expectedGroup.Count, actualGroup.Count);
            for (var i = 0; i < count; i++)
            {
                var expectedReference = i < expectedGroup.Count ? expectedGroup[i] : null;
                var actualReference = i < actualGroup.Count ? actualGroup[i] : null;
                var path = $"{projectPath}.MetadataReferences[{name}{GetIndexSuffix(count, i)}]";
                if (expectedReference is null || actualReference is null)
                {
                    builder.Add(new(path, expectedReference?.Display, actualReference?.Display));
                    continue;
                }

                CompareValue(builder, $"{path}.Display", expectedReference.Display, actualReference.Display);
                CompareValue(builder, $"{path}.Aliases", FormatAliases(expectedReference.Properties.Aliases), FormatAliases(actualReference.Properties.Aliases));
                CompareValue(builder, $"{path}.EmbedInteropTypes", expectedReference.Properties.EmbedInteropTypes, actualReference.Properties.EmbedInteropTypes);
                CompareValue(builder, $"{path}.Kind", expectedReference.Properties.Kind, actualReference.Properties.Kind);
            }
        }
    }

    private static void CompareAnalyzerReferences(
        ImmutableArray<WorkspaceDifference>.Builder builder,
        string projectPath,
        IEnumerable<AnalyzerReference> expected,
        IEnumerable<AnalyzerReference> actual)
    {
        var expectedReferences = expected.GroupBy(GetAnalyzerName, PathComparer).ToDictionary(x => x.Key, x => x.OrderBy(GetAnalyzerSortKey, PathComparer).ToList(), PathComparer);
        var actualReferences = actual.GroupBy(GetAnalyzerName, PathComparer).ToDictionary(x => x.Key, x => x.OrderBy(GetAnalyzerSortKey, PathComparer).ToList(), PathComparer);
        CompareValue(builder, $"{projectPath}.AnalyzerReferences.Count", expectedReferences.Sum(x => x.Value.Count), actualReferences.Sum(x => x.Value.Count));

        foreach (var name in expectedReferences.Keys.Union(actualReferences.Keys, PathComparer).OrderBy(x => x, PathComparer))
        {
            expectedReferences.TryGetValue(name, out var expectedGroup);
            actualReferences.TryGetValue(name, out var actualGroup);
            expectedGroup ??= [];
            actualGroup ??= [];
            var count = Math.Max(expectedGroup.Count, actualGroup.Count);
            for (var i = 0; i < count; i++)
            {
                var expectedReference = i < expectedGroup.Count ? expectedGroup[i] : null;
                var actualReference = i < actualGroup.Count ? actualGroup[i] : null;
                var path = $"{projectPath}.AnalyzerReferences[{name}{GetIndexSuffix(count, i)}]";
                if (expectedReference is null || actualReference is null)
                {
                    builder.Add(new(path, expectedReference?.FullPath ?? expectedReference?.Display, actualReference?.FullPath ?? actualReference?.Display));
                    continue;
                }

                CompareValue(builder, $"{path}.Display", expectedReference.Display, actualReference.Display);
                CompareValue(builder, $"{path}.FullPath", expectedReference.FullPath, actualReference.FullPath);
            }
        }
    }

    private static void CompareCompilationOptions(
        ImmutableArray<WorkspaceDifference>.Builder builder,
        string projectPath,
        CompilationOptions? expected,
        CompilationOptions? actual)
    {
        var path = $"{projectPath}.CompilationOptions";
        CompareValue(builder, $"{path}.Type", expected?.GetType().FullName, actual?.GetType().FullName);
        if (expected is null || actual is null)
        {
            return;
        }

        CompareValue(builder, $"{path}.OutputKind", expected.OutputKind, actual.OutputKind);
        CompareValue(builder, $"{path}.ModuleName", expected.ModuleName, actual.ModuleName);
        CompareValue(builder, $"{path}.MainTypeName", expected.MainTypeName, actual.MainTypeName);
        CompareValue(builder, $"{path}.ScriptClassName", expected.ScriptClassName, actual.ScriptClassName);
        CompareValue(builder, $"{path}.OptimizationLevel", expected.OptimizationLevel, actual.OptimizationLevel);
        CompareValue(builder, $"{path}.CheckOverflow", expected.CheckOverflow, actual.CheckOverflow);
        CompareValue(builder, $"{path}.Platform", expected.Platform, actual.Platform);
        CompareValue(builder, $"{path}.GeneralDiagnosticOption", expected.GeneralDiagnosticOption, actual.GeneralDiagnosticOption);
        CompareValue(builder, $"{path}.WarningLevel", expected.WarningLevel, actual.WarningLevel);
        CompareValue(builder, $"{path}.ConcurrentBuild", expected.ConcurrentBuild, actual.ConcurrentBuild);
        CompareValue(builder, $"{path}.Deterministic", expected.Deterministic, actual.Deterministic);
        CompareValue(builder, $"{path}.CryptoKeyFile", expected.CryptoKeyFile, actual.CryptoKeyFile);
        CompareValue(builder, $"{path}.CryptoKeyContainer", expected.CryptoKeyContainer, actual.CryptoKeyContainer);
        CompareValue(builder, $"{path}.DelaySign", expected.DelaySign, actual.DelaySign);
        CompareValue(builder, $"{path}.PublicSign", expected.PublicSign, actual.PublicSign);
        CompareValue(builder, $"{path}.MetadataImportOptions", expected.MetadataImportOptions, actual.MetadataImportOptions);
        CompareValue(builder, $"{path}.SpecificDiagnosticOptions", FormatMap(expected.SpecificDiagnosticOptions), FormatMap(actual.SpecificDiagnosticOptions));

        if (expected is CSharpCompilationOptions expectedCSharp && actual is CSharpCompilationOptions actualCSharp)
        {
            CompareValue(builder, $"{path}.AllowUnsafe", expectedCSharp.AllowUnsafe, actualCSharp.AllowUnsafe);
            CompareValue(builder, $"{path}.NullableContextOptions", expectedCSharp.NullableContextOptions, actualCSharp.NullableContextOptions);
            CompareValue(builder, $"{path}.Usings", string.Join(",", expectedCSharp.Usings), string.Join(",", actualCSharp.Usings));
        }

        if (expected is VisualBasicCompilationOptions expectedVisualBasic && actual is VisualBasicCompilationOptions actualVisualBasic)
        {
            CompareValue(builder, $"{path}.OptionExplicit", expectedVisualBasic.OptionExplicit, actualVisualBasic.OptionExplicit);
            CompareValue(builder, $"{path}.OptionInfer", expectedVisualBasic.OptionInfer, actualVisualBasic.OptionInfer);
            CompareValue(builder, $"{path}.OptionStrict", expectedVisualBasic.OptionStrict, actualVisualBasic.OptionStrict);
            CompareValue(builder, $"{path}.OptionCompareText", expectedVisualBasic.OptionCompareText, actualVisualBasic.OptionCompareText);
            CompareValue(builder, $"{path}.RootNamespace", expectedVisualBasic.RootNamespace, actualVisualBasic.RootNamespace);
            CompareValue(builder, $"{path}.GlobalImports", string.Join(",", expectedVisualBasic.GlobalImports), string.Join(",", actualVisualBasic.GlobalImports));
        }
    }

    private static void CompareParseOptions(
        ImmutableArray<WorkspaceDifference>.Builder builder,
        string projectPath,
        ParseOptions? expected,
        ParseOptions? actual)
    {
        var path = $"{projectPath}.ParseOptions";
        CompareValue(builder, $"{path}.Type", expected?.GetType().FullName, actual?.GetType().FullName);
        if (expected is null || actual is null)
        {
            return;
        }

        CompareValue(builder, $"{path}.Kind", expected.Kind, actual.Kind);
        CompareValue(builder, $"{path}.DocumentationMode", expected.DocumentationMode, actual.DocumentationMode);
        CompareValue(builder, $"{path}.Features", FormatMap(expected.Features), FormatMap(actual.Features));
        if (expected is CSharpParseOptions expectedCSharp && actual is CSharpParseOptions actualCSharp)
        {
            CompareValue(builder, $"{path}.LanguageVersion", expectedCSharp.LanguageVersion, actualCSharp.LanguageVersion);
            CompareValue(builder, $"{path}.PreprocessorSymbols", string.Join(",", expectedCSharp.PreprocessorSymbolNames.OrderBy(x => x, StringComparer.Ordinal)), string.Join(",", actualCSharp.PreprocessorSymbolNames.OrderBy(x => x, StringComparer.Ordinal)));
        }

        if (expected is VisualBasicParseOptions expectedVisualBasic && actual is VisualBasicParseOptions actualVisualBasic)
        {
            CompareValue(builder, $"{path}.LanguageVersion", expectedVisualBasic.LanguageVersion, actualVisualBasic.LanguageVersion);
            CompareValue(builder, $"{path}.PreprocessorSymbols", FormatMap(expectedVisualBasic.PreprocessorSymbols), FormatMap(actualVisualBasic.PreprocessorSymbols));
        }
    }

    private static void CompareValue<T>(
        ImmutableArray<WorkspaceDifference>.Builder builder,
        string path,
        T expected,
        T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            builder.Add(new(path, expected?.ToString(), actual?.ToString()));
        }
    }

    private static string GetProjectPath(string filePath, Project project) =>
        $"Projects[{filePath}|{project.OutputFilePath ?? project.Name}]";

    private static string GetProjectSortKey(Project? project) =>
        project is null ? "" : $"{project.FilePath}|{project.OutputFilePath}|{project.Name}|{project.Language}";

    private static string GetProjectReferenceKey(Project? project) =>
        project is null
            ? "<unresolved>"
            : $"{project.FilePath}|{GetTargetFrameworkMoniker(project) ?? project.OutputFilePath ?? project.Name}";

    private static string? GetTargetFrameworkMoniker(Project project)
    {
        if (project.OutputFilePath is { } outputFilePath)
        {
            foreach (var part in outputFilePath
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
                .AsEnumerable()
                .Reverse())
            {
                if (part.StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
                    part.Skip(3).Any(char.IsDigit))
                {
                    return part;
                }
            }
        }

        var openParen = project.Name.LastIndexOf('(');
        return openParen >= 0 && project.Name.EndsWith(")", StringComparison.Ordinal)
            ? project.Name.Substring(openParen + 1, project.Name.Length - openParen - 2)
            : null;
    }

    private static string DescribeProject(Project project) =>
        $"{project.Name}|{project.Language}|{project.FilePath}|{project.OutputFilePath}";

    private static string DescribeDocument(TextDocument document) =>
        $"{document.Name}|{document.FilePath}|{string.Join("/", document.Folders)}";

    private static string GetReferenceSortKey(MetadataReference reference) =>
        $"{GetReferenceName(reference)}|{reference.Display}|{FormatAliases(reference.Properties.Aliases)}";

    private static string GetReferenceName(MetadataReference reference) =>
        Path.GetFileName(reference.Display) ?? reference.Display ?? "<no display>";

    private static string GetAnalyzerSortKey(AnalyzerReference reference) =>
        $"{GetAnalyzerName(reference)}|{reference.FullPath}|{reference.Display}";

    private static string GetAnalyzerName(AnalyzerReference reference) =>
        Path.GetFileNameWithoutExtension(reference.FullPath ?? reference.Display) ?? "<no display>";

    private static string FormatAliases(ImmutableArray<string> aliases) =>
        aliases.IsDefaultOrEmpty ? "" : string.Join(",", aliases.OrderBy(x => x, StringComparer.Ordinal));

    private static string GetIndexSuffix(int count, int index) => count > 1 ? $"#{index}" : "";

    private static string FormatMap<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> map) where TKey : notnull =>
        string.Join(",", map.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));
}
