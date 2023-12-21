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

    private void TestExport(int expectedCount, Action<string>? verifyExportCallback = null, bool runBuild = true)
    {
        using var scratchDir = new TempDir("export test");
        var binlogFilePath = Path.Combine(RootDirectory, "msbuild.binlog");
        var compilerLogFilePath = Path.Combine(scratchDir.DirectoryPath, "build.complog");
        var diagnosticList = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, compilerLogFilePath);
        Assert.Empty(diagnosticList);

        // Now that we've converted to a compiler log delete all the original project code. This 
        // ensures our builds below don't succeed because old files are being referenced
        Root.EmptyDirectory();

        TestExport(compilerLogFilePath, expectedCount, verifyExportCallback: verifyExportCallback, runBuild: runBuild);
    }

    private void TestExport(string compilerLogFilePath, int? expectedCount, bool includeAnalyzers = true, Action<string>? verifyExportCallback = null, bool runBuild = true) =>
        TestExport(TestOutputHelper, compilerLogFilePath, expectedCount, includeAnalyzers, verifyExportCallback, runBuild);

    internal static void TestExport(ITestOutputHelper testOutputHelper, string compilerLogFilePath, int? expectedCount, bool includeAnalyzers = true, Action<string>? verifyExportCallback = null, bool runBuild = true)
    {
        using var reader = CompilerLogReader.Create(compilerLogFilePath);
#if NETCOREAPP
        var sdkDirs = SdkUtil.GetSdkDirectories();
#else
        var sdkDirs = SdkUtil.GetSdkDirectories(@"c:\Program Files\dotnet");
#endif
        var exportUtil = new ExportUtil(reader, includeAnalyzers);
        var count = 0;
        foreach (var compilerCall in reader.ReadAllCompilerCalls())
        {
            count++;
            testOutputHelper.WriteLine($"Testing export for {compilerCall.ProjectFileName} - {compilerCall.TargetFramework}");
            using var tempDir = new TempDir();
            exportUtil.Export(compilerCall, tempDir.DirectoryPath, sdkDirs);

            if (runBuild)
            {
                // Now run the generated build.cmd and see if it succeeds;
                var buildResult = RunBuildCmd(tempDir.DirectoryPath);
                testOutputHelper.WriteLine(buildResult.StandardOut);
                testOutputHelper.WriteLine(buildResult.StandardError);
                Assert.True(buildResult.Succeeded, $"Cannot build {Path.GetFileName(compilerLogFilePath)}");
            }

            // Ensure that full paths aren't getting written out to the RSP file. That makes the 
            // build non-xcopyable. 
            foreach (var line in File.ReadAllLines(Path.Combine(tempDir.DirectoryPath, "build.rsp")))
            {
                Assert.False(line.Contains(tempDir.DirectoryPath, StringComparison.OrdinalIgnoreCase), $"Has full path: {line}");
            }

            verifyExportCallback?.Invoke(tempDir.DirectoryPath);
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

    /// <summary>
    /// Make sure that generated files are put into the generated directory
    /// </summary>
    [Fact]
    public void GeneratedText()
    {
        TestExport(Fixture.ConsoleComplogPath.Value, 1, verifyExportCallback: tempPath =>
        {
            var generatedPath = Path.Combine(tempPath, "generated");
            var files = Directory.GetFiles(generatedPath, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
        }, runBuild: false);
    }

    /// <summary>
    /// Make sure the rsp file has the expected structure when we exclude analyzers from the 
    /// export.
    /// </summary>
    [Fact]
    public void GeneratedTextExcludeAnalyzers()
    {
        TestExport(Fixture.ConsoleComplogPath.Value, 1, includeAnalyzers: false, verifyExportCallback: tempPath =>
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
            Assert.Empty(analyzers);
        }, runBuild: false);
    }

    [Fact]
    public void ConsoleMultiTarget()
    {
        TestExport(Fixture.ClassLibMultiComplogPath.Value, expectedCount: 2, runBuild: false);
    }

    [Fact]
    public void ConsoleWithRuleset()
    {
        TestExport(Fixture.ConsoleComplexComplogPath.Value, expectedCount: 1, verifyExportCallback: void (string path) =>
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
        }, runBuild: false);
    }

    [Fact]
    public void StrongNameKey()
    {
        TestExport(Fixture.ConsoleSignedComplogPath.Value, expectedCount: 1, runBuild: false);
    }

    [Fact]
    public void ExportAll()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibMultiComplogPath.Value);
        var exportUtil = new ExportUtil(reader, includeAnalyzers: false);
        exportUtil.ExportAll(RootDirectory, SdkUtil.GetSdkDirectories());
        Assert.True(Directory.Exists(Path.Combine(RootDirectory, "0")));
        Assert.True(Directory.Exists(Path.Combine(RootDirectory, "1")));
    }

    [Fact]
    public void ExportAllBadPath()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibMultiComplogPath.Value);
        var exportUtil = new ExportUtil(reader, includeAnalyzers: false);
        Assert.Throws<ArgumentException>(() => exportUtil.ExportAll(@"relative/path", SdkUtil.GetSdkDirectories()));
    }

    private void EmbedLineCore(string contentFilePath)
    {
        RunDotNet($"new console --name example --output .");
        AddProjectProperty("<EmbedAllSources>true</EmbedAllSources>");
        File.WriteAllText(Path.Combine(RootDirectory, "Util.cs"),
            $"""
        #line 42 "{contentFilePath}"
        """);
        RunDotNet("build -bl -nr:false");
        TestExport(1);
    }

    [Fact]
    public void EmbedLineInsideProject()
    {
        // Relative
        _ = Root.NewFile("content.txt", "this is some content");
        EmbedLineCore("content.txt");
    }

    [Fact]
    public void ExportRsp1()
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

    [Fact]
    public void ExportRsp2()
    {
        var args = new[]
        {
            "blah.cs",
            @"/embed:""c:\blah\a,b=net472.cs""",
        };

        using var writer = new StringWriter();
        ExportUtil.ExportRsp(args, writer);
        Assert.Equal("""
            blah.cs
            /embed:"c:\blah\a,b=net472.cs"

            """, writer.ToString());
    }
}
