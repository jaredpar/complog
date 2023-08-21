using Basic.CompilerLog.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class ExportUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public ExportUtilTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(ExportUtilTests))
    {
        Fixture = fixture;
    }

    private void TestExport(int expectedCount, Action<string>? callback = null)
    {
        using var scratchDir = new TempDir("export test");
        var binlogFilePath = Path.Combine(RootDirectory, "msbuild.binlog");
        var compilerLogFilePath = Path.Combine(scratchDir.DirectoryPath, "build.complog");
        var diagnosticList = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, compilerLogFilePath);
        Assert.Empty(diagnosticList);

        // Now that we've converted to a compiler log delete all the original project code. This 
        // ensures our builds below don't succeed because old files are being referenced
        Root.EmptyDirectory();

        TestExport(compilerLogFilePath, expectedCount, callback: callback);
    }

    private void TestExport(string compilerLogFilePath, int? expectedCount, bool includeAnalyzers = true, Action<string>? callback = null)
    {
        using var reader = CompilerLogReader.Create(compilerLogFilePath);
#if NETCOREAPP
        var sdkDirs = DotnetUtil.GetSdkDirectories();
#else
        var sdkDirs = DotnetUtil.GetSdkDirectories(@"c:\Program Files\dotnet");
#endif
        var exportUtil = new ExportUtil(reader, includeAnalyzers);
        var count = 0;
        foreach (var compilerCall in reader.ReadAllCompilerCalls())
        {
            count++;
            TestOutputHelper.WriteLine($"Testing export for {compilerCall.ProjectFileName} - {compilerCall.TargetFramework}");
            using var tempDir = new TempDir();
            exportUtil.Export(compilerCall, tempDir.DirectoryPath, sdkDirs);

            // Now run the generated build.cmd and see if it succeeds;
            var buildResult = RunBuildCmd(tempDir.DirectoryPath);
            TestOutputHelper.WriteLine(buildResult.StandardOut);
            TestOutputHelper.WriteLine(buildResult.StandardError);
            Assert.True(buildResult.Succeeded, $"Cannot build {Path.GetFileName(compilerLogFilePath)}");

            // Ensure that full paths aren't getting written out to the RSP file. That makes the 
            // build non-xcopyable. 
            foreach (var line in File.ReadAllLines(Path.Combine(tempDir.DirectoryPath, "build.rsp")))
            {
                Assert.False(line.Contains(tempDir.DirectoryPath, StringComparison.OrdinalIgnoreCase), $"Has full path: {line}");
            }

            callback?.Invoke(tempDir.DirectoryPath);
        }

        if (expectedCount is { } ec)
        {
            Assert.Equal(ec, count);
        }
        else
        {
            Assert.True(count> 0);
        }
    }

    [Fact]
    public void Console()
    {
        TestExport(Fixture.ConsoleComplogPath.Value, 1);
    }

    [Fact]
    public void ClassLib()
    {
        TestExport(Fixture.ClassLibComplogPath.Value, 1);
    }

    /// <summary>
    /// Make sure that generated files are put into the generated directory
    /// </summary>
    [Fact]
    public void GeneratedText()
    {
        TestExport(Fixture.ConsoleComplogPath.Value, 1, callback: tempPath =>
        {
            var generatedPath = Path.Combine(tempPath, "generated");
            var files = Directory.GetFiles(generatedPath, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
        });
    }

    /// <summary>
    /// Make sure the rsp file has the expected structure when we exclude analyzers from the 
    /// export.
    /// </summary>
    [Fact]
    public void GeneratedTextExcludeAnalyzers()
    {
        TestExport(Fixture.ConsoleComplogPath.Value, 1, includeAnalyzers: false, callback: tempPath =>
        {
            var rspPath = Path.Combine(tempPath, "build.rsp");
            var foundPath = false;
            foreach (var line in File.ReadAllLines(rspPath))
            {
                Assert.DoesNotContain("/analyzer:", line);
                if (line.Contains("RegexGenerator.g.cs") && !line.StartsWith("/"))
                {
                    foundPath = true;
                }
            }

            Assert.True(foundPath);

            var analyzers = Directory.GetFiles(Path.Combine(tempPath, "analyzers"), "*.dll", SearchOption.AllDirectories).ToList();
            Assert.Equal(7, analyzers.Count);
        });
    }

    [Fact]
    public void ConsoleMultiTarget()
    {
        RunDotNet($"new console --name example --output .");
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFrameworks>net7.0;net6.0</TargetFrameworks>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
            </Project>
            """);
        RunDotNet("build -bl");
        TestExport(2);
    }

    [Fact]
    public void ConsoleWithResource()
    {
        RunDotNet($"new console --name example --output .");
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net7.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <EmbeddedResource Include="resource.txt" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(RootDirectory, "resource.txt"), """
            This is an awesome resource
            """);
        RunDotNet("build -bl");
        TestExport(1);
    }

    [Fact]
    public void ConsoleWithSpaceInSourceName()
    {
        RunDotNet($"new console --name example --output .");
        File.WriteAllText(Path.Combine(RootDirectory, "code file.cs"), """
            class C { }
            """);
        RunDotNet("build -bl");
        TestExport(1);
    }

    [Fact]
    public void ConsoleWithRuleset()
    {
        RunDotNet($"new console --name console-with-ruleset --output .");
        File.WriteAllText(Path.Combine(RootDirectory, "console-with-ruleset.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net7.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <CodeAnalysisRuleset>example.ruleset</CodeAnalysisRuleset>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(RootDirectory, "example.ruleset"), """
            <RuleSet Name="Rules for Hello World project" Description="These rules focus on critical issues for the Hello World app." ToolsVersion="10.0">
            <Localization ResourceAssembly="Microsoft.VisualStudio.CodeAnalysis.RuleSets.Strings.dll" ResourceBaseName="Microsoft.VisualStudio.CodeAnalysis.RuleSets.Strings.Localized">
                <Name Resource="HelloWorldRules_Name" />
                <Description Resource="HelloWorldRules_Description" />
            </Localization>
            <Rules AnalyzerId="Microsoft.Analyzers.ManagedCodeAnalysis" RuleNamespace="Microsoft.Rules.Managed">
                <Rule Id="CA1001" Action="Warning" />
                <Rule Id="CA1009" Action="Warning" />
                <Rule Id="CA1016" Action="Warning" />
                <Rule Id="CA1033" Action="Warning" />
            </Rules>
            <Rules AnalyzerId="Microsoft.CodeQuality.Analyzers" RuleNamespace="Microsoft.CodeQuality.Analyzers">
                <Rule Id="CA1802" Action="Error" />
                <Rule Id="CA1814" Action="Info" />
                <Rule Id="CA1823" Action="None" />
                <Rule Id="CA2217" Action="Warning" />
            </Rules>
            </RuleSet>
            """);
        RunDotNet("build -bl");
        TestExport(expectedCount: 1, void (string path) =>
        {
            var found = false;
            var expected = $"/ruleset:{Path.Combine("src", "example.ruleset")}";
            foreach (var line in File.ReadAllLines(Path.Combine(path, "build.rsp")))
            {
                if (line == expected)
                {
                    found = true;
                    break;
                }
            }

            Assert.True(found);
        });
    }

    [Fact]
    public void ContentWin32Elements()
    {
        RunDotNet($"new console --name example --output .");
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net7.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <Win32Manifest>resource.txt</Win32Manifest>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(RootDirectory, "resource.txt"), """
            This is an awesome resource
            """);
        RunDotNet("build -bl");
        TestExport(1);
    }

    [Fact]
    public void StrongNameKey()
    {
        RunDotNet($"new console --name example --output .");
        AddProjectProperty("<PublicSign>true</PublicSign>");
        AddProjectProperty("<KeyOriginatorFile>key.snk</KeyOriginatorFile>");
        var keyBytes = ResourceLoader.GetResourceBlob("Key.snk");
        File.WriteAllBytes(Path.Combine(RootDirectory, "key.snk"), keyBytes);
        RunDotNet("build -bl");
        TestExport(1);
    }

    [Fact]
    public void EmbedLineOutsidePath()
    {
        using var temp = new TempDir();
        var contentFilePath = temp.NewFile("content.txt", "this is some content");
        RunDotNet($"new console --name example --output .");
        AddProjectProperty("<EmbedAllSources>true</EmbedAllSources>");
        File.WriteAllText(Path.Combine(RootDirectory, "Util.cs"),
            $"""
            #line 42 "{contentFilePath}"
            """);
        RunDotNet("build -bl");
        temp.Dispose();
        TestExport(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllCompilerLogs(bool includeAnalyzers)
    {
        foreach (var complogPath in Fixture.GetAllCompLogs())
        {
            TestExport(complogPath, expectedCount: null, includeAnalyzers);
        }
    }

    [Fact]
    public void ExportRsp()
    {
        var args = new[]
        {
            "blah .cs",
            "/r:blah .cs", // only change non-options as options quotes handled specially by command line parser
            "a b.cs",
            "ab.cs",
        };

        using var writer = new StringWriter();
        ExportUtil.ExportRsp(args, writer);
        Assert.Equal("""
            "blah .cs"
            /r:blah .cs
            "a b.cs"
            ab.cs

            """, writer.ToString());

        writer.GetStringBuilder().Length = 0;
        ExportUtil.ExportRsp(args, writer, singleLine: true);
        Assert.Equal(@"""blah .cs"" /r:blah .cs ""a b.cs"" ab.cs", writer.ToString()); 
    }

}
