using Basic.CompilerLog.Util;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class ExportProjectUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public ExportProjectUtilTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(ExportProjectUtilTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void ConsoleProjectExport()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath);
        var compilerCalls = reader.ReadAllCompilerCalls();

        using var tempDir = new TempDir();
        var exportUtil = new ExportProjectUtil(reader);
        exportUtil.ExportProject(compilerCalls, tempDir.DirectoryPath);

        var solutionPath = Path.Combine(tempDir.DirectoryPath, "export.slnx");
        var projectPath = Path.Combine(tempDir.DirectoryPath, "src", "console", "console.csproj");

        Assert.True(File.Exists(solutionPath));
        Assert.True(File.Exists(projectPath));

        var expectedSolution = """
            <?xml version="1.0" encoding="utf-8" standalone="yes"?>
            <Solution>
              <Project Path="src/console/console.csproj" />
            </Solution>
            """;
        Assert.Equal(expectedSolution, NormalizeLineEndings(File.ReadAllText(solutionPath)));

        var expectedProject = $"""
            <?xml version="1.0" encoding="utf-8" standalone="yes"?>
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{TestUtil.TestTargetFramework}</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="generated/group0/RegexGenerator.g.cs" />
              </ItemGroup>
              <ItemGroup>
                <EditorConfigFiles Include="misc/root/.dotnet/sdk/10.0.102/Sdks/Microsoft.NET.Sdk/analyzers/build/config/analysislevel_9_default.globalconfig" />
                <EditorConfigFiles Include="obj/Debug/net9.0/console.GeneratedMSBuildEditorConfig.editorconfig" />
              </ItemGroup>
            </Project>
            """;
        Assert.Equal(expectedProject, NormalizeLineEndings(File.ReadAllText(projectPath)));
    }

    [Fact]
    public void ProjectReferenceExport()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleWithReference.Value.CompilerLogPath);
        var compilerCalls = reader.ReadAllCompilerCalls();

        using var tempDir = new TempDir();
        var exportUtil = new ExportProjectUtil(reader);
        exportUtil.ExportProject(compilerCalls, tempDir.DirectoryPath);

        var consoleProject = Directory.GetFiles(tempDir.DirectoryPath, "console-with-reference.csproj", SearchOption.AllDirectories).Single();
        var classLibProject = Directory.GetFiles(tempDir.DirectoryPath, "util.csproj", SearchOption.AllDirectories).Single();

        var consoleDoc = XDocument.Load(consoleProject);
        var projectReferences = consoleDoc.Descendants("ProjectReference").Select(element => element.Attribute("Include")?.Value).Where(value => value is not null).ToList();
        Assert.Single(projectReferences);

        var expectedReference = Path.Combine("..", Path.GetFileNameWithoutExtension(classLibProject), Path.GetFileName(classLibProject));
        Assert.Equal(NormalizePath(expectedReference), NormalizePath(projectReferences[0]!));
    }

    [Fact]
    public void AdditionalFilesExport()
    {
        using var reader = CompilerLogReader.Create(Fixture.ConsoleComplex.Value.CompilerLogPath);
        var compilerCalls = reader.ReadAllCompilerCalls();

        using var tempDir = new TempDir();
        var exportUtil = new ExportProjectUtil(reader);
        exportUtil.ExportProject(compilerCalls, tempDir.DirectoryPath);

        var projectPath = Directory.GetFiles(tempDir.DirectoryPath, "console-complex.csproj", SearchOption.AllDirectories).Single();
        var projectDoc = XDocument.Load(projectPath);
        var additionalFiles = projectDoc.Descendants("AdditionalFiles").Select(element => element.Attribute("Include")?.Value).Where(value => value is not null).ToList();
        Assert.Contains("additional.txt", additionalFiles);

        var additionalFilePath = Path.Combine(Path.GetDirectoryName(projectPath)!, "additional.txt");
        Assert.True(File.Exists(additionalFilePath));
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n");

    private static string NormalizePath(string value) =>
        value.Replace('\\', '/');
}
