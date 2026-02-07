using Basic.CompilerLog.Util;
using System.IO;
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
            </Project>
            """;
        Assert.Equal(expectedProject, NormalizeLineEndings(File.ReadAllText(projectPath)));
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n");
}
