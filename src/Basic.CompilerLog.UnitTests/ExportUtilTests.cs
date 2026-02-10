using Basic.CompilerLog.Util;
using Basic.Reference.Assemblies;
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

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class ExportUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public ExportUtilTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(ExportUtilTests))
    {
        Fixture = fixture;
    }

    private void TestExport(
        int expectedCount,
        Action<string>? verifyExportCallback = null,
        bool runBuild = true,
        Action<ProcessResult>? verifyBuildResult = null)
    {
        using var scratchDir = new TempDir("export test");
        var binlogFilePath = Path.Combine(RootDirectory, "msbuild.binlog");
        var compilerLogFilePath = Path.Combine(scratchDir.DirectoryPath, "build.complog");
        var diagnosticList = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, compilerLogFilePath);
        Assert.Empty(diagnosticList);

        // Now that we've converted to a compiler log delete all the original project code. This
        // ensures our builds below don't succeed because old files are being referenced
        Root.EmptyDirectory();

        TestExport(
            compilerLogFilePath,
            expectedCount,
            verifyExportCallback: verifyExportCallback,
            runBuild: runBuild,
            verifyBuildResult: verifyBuildResult);
    }

    private void TestExport(
        string compilerLogFilePath,
        int? expectedCount,
        bool excludeAnalyzers = false,
        Action<string>? verifyExportCallback = null,
        bool runBuild = true,
        Action<ProcessResult>? verifyBuildResult = null) =>
        TestExport(
            TestOutputHelper,
            compilerLogFilePath,
            expectedCount,
            excludeAnalyzers,
            verifyExportCallback,
            runBuild,
            verifyBuildResult);

    internal static void TestExport(
        ITestOutputHelper testOutputHelper,
        string compilerLogFilePath,
        int? expectedCount,
        bool excludeAnalyzers = false,
        Action<string>? verifyExportCallback = null,
        bool runBuild = true,
        Action<ProcessResult>? verifyBuildResult = null)
    {
        using var reader = CompilerLogReader.Create(compilerLogFilePath);
        TestExport(
            testOutputHelper,
            reader: reader,
            expectedCount,
            excludeAnalyzers,
            verifyExportCallback,
            runBuild,
            verifyBuildResult);
    }

    internal static void TestExport(
        ITestOutputHelper testOutputHelper,
        CompilerLogReader reader,
        int? expectedCount,
        bool excludeAnalyzers = false,
        Action<string>? verifyExportCallback = null,
        bool runBuild = true,
        Action<ProcessResult>? verifyBuildResult = null)
    {
#if NET
        var compilerDirectories = SdkUtil.GetSdkCompilerDirectories();
#else
        var compilerDirectories = SdkUtil.GetSdkCompilerDirectories(@"c:\Program Files\dotnet");
#endif
        var exportUtil = new ExportUtil(reader, excludeAnalyzers);
        var count = 0;
        foreach (var compilerCall in reader.ReadAllCompilerCalls())
        {
            count++;
            testOutputHelper.WriteLine($"Testing export for {compilerCall.ProjectFileName} - {compilerCall.TargetFramework}");
            using var tempDir = new TempDir();
            exportUtil.Export(compilerCall, tempDir.DirectoryPath, compilerDirectories);

            if (runBuild)
            {
                var buildScript = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "build.cmd" : "build.sh";
                testOutputHelper.WriteLine("Build script");
                testOutputHelper.WriteLine(File.ReadAllText(Path.Combine(tempDir.DirectoryPath, buildScript)));

                // Now run the generated build.cmd and see if it succeeds;
                var buildResult = TestUtil.RunBuildCmd(tempDir.DirectoryPath);
                testOutputHelper.WriteLine(buildResult.StandardOut);
                testOutputHelper.WriteLine(buildResult.StandardError);
                verifyBuildResult?.Invoke(buildResult);
                Assert.True(buildResult.Succeeded, $"Cannot build {compilerCall.ProjectFileName}: {buildResult.StandardOut}");
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
            Assert.True(count > 0);
        }
    }

    /// <summary>
    /// Make sure that generated files are put into the generated directory
    /// </summary>
    [Fact]
    public void GeneratedText()
    {
        TestExport(Fixture.Console.Value.CompilerLogPath, 1, verifyExportCallback: tempPath =>
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
        TestExport(Fixture.Console.Value.CompilerLogPath, 1, excludeAnalyzers: true, verifyExportCallback: tempPath =>
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

    /// <summary>
    /// Make sure that global configs get their full paths mapped to the new location on disk
    /// </summary>
    [Fact]
    public void GlobalConfigMapsPaths()
    {
        TestExport(Fixture.ConsoleComplex.Value.CompilerLogPath, expectedCount: 1, verifyExportCallback: void (string path) =>
        {
            var configFilePath = Directory
                .EnumerateFiles(path, "console-complex.GeneratedMSBuildEditorConfig.editorconfig", SearchOption.AllDirectories)
                .Single();

            var found = false;
            var pattern = $"[{RoslynUtil.EscapeEditorConfigSectionPath(path)}";
            foreach (var line in File.ReadAllLines(configFilePath))
            {
                if (line.StartsWith(pattern, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            Assert.True(found);
        }, runBuild: true);
    }

    [Fact]
    public void ConsoleMultiTarget()
    {
        TestExport(Fixture.ClassLibMulti.Value.CompilerLogPath, expectedCount: 2, runBuild: false);
    }

    [Fact]
    public void ConsoleWithRuleset()
    {
        TestExport(Fixture.ConsoleComplex.Value.CompilerLogPath, expectedCount: 1, verifyExportCallback: void (string path) =>
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

    /// <summary>
    /// Make sure that we can round trip a /link argument. That is a reference that we are embedding
    /// interop types for.
    /// </summary>
    [Fact]
    public void ConsoleWithLink()
    {
        var piaInfo = LibraryUtil.GetSimplePia();
        var linkFilePath = Root.NewFile(piaInfo.FileName, piaInfo.Image);

        using var reader = CreateReader(builder =>
        {
            using var binlogReader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!);
            var compilerCall = binlogReader.ReadAllCompilerCalls().Single();
            var arguments = binlogReader.ReadArguments(compilerCall);
            arguments = [.. arguments, $"/link:{linkFilePath}"];
            builder.AddFromDisk(compilerCall, arguments);

            var commandLineArgs = BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall, arguments);
            Assert.True(commandLineArgs.MetadataReferences.Any(x => x.Properties.EmbedInteropTypes));
        });

        TestExport(TestOutputHelper, reader, expectedCount: 1, verifyExportCallback: tempPath =>
        {
            var rspPath = Path.Combine(tempPath, "build.rsp");
            var foundPath = false;
            foreach (var line in File.ReadAllLines(rspPath))
            {
                if (line.StartsWith("/link:", StringComparison.Ordinal))
                {
                    foundPath = true;
                    Assert.Equal($@"/link:ref{Path.DirectorySeparatorChar}{piaInfo.FileName}", line);
                }
            }

            Assert.True(foundPath);
        }, runBuild: true);
    }

    [Fact]
    public void StrongNameKey()
    {
        TestExport(Fixture.ConsoleSigned.Value.CompilerLogPath, expectedCount: 1, runBuild: false);
    }

#if NET
    [Fact]
    public void ExportAll()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibMulti.Value.CompilerLogPath);
        var exportUtil = new ExportUtil(reader, excludeAnalyzers: true);
        exportUtil.ExportAll(RootDirectory, SdkUtil.GetSdkCompilerDirectories());
        Assert.True(Directory.Exists(Path.Combine(RootDirectory, "0")));
        Assert.True(Directory.Exists(Path.Combine(RootDirectory, "1")));
    }

    [Fact]
    public void ExportAllBadPath()
    {
        using var reader = CompilerLogReader.Create(Fixture.ClassLibMulti.Value.CompilerLogPath);
        var exportUtil = new ExportUtil(reader, excludeAnalyzers: true);
        Assert.Throws<ArgumentException>(() => exportUtil.ExportAll(@"relative/path", SdkUtil.GetSdkCompilerDirectories()));
    }
#endif

    [Fact]
    public void ExportWithUnsafeOption()
    {
        RunDotNet("new console --name example --output .");

        var projectFileContent = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(RootDirectory, "example.csproj"), projectFileContent, DefaultEncoding);

        var codeContent = """
            unsafe class C { }
            """;
        File.WriteAllText(Path.Combine(RootDirectory, "Code.cs"), codeContent, DefaultEncoding);

        RunDotNet("build -bl -nr:false");

        var binlog = Path.Combine(RootDirectory, "msbuild.binlog");
        var complog = Path.Combine(RootDirectory, "msbuild.complog");
        var result = CompilerLogUtil.TryConvertBinaryLog(binlog, complog);
        Assert.True(result.Succeeded);

        TestExport(
            compilerLogFilePath: complog,
            expectedCount: 1,
            excludeAnalyzers: true,
            runBuild: true);
    }

    /// <summary>
    /// <c>/noconfig</c> should not be part of <c>.rsp</c> files
    /// (the compiler gives a warning if it is and ignores the option).
    /// </summary>
    [Fact]
    public void ExportNoconfig()
    {
        RunDotNet("new console --name example --output .");
        RunDotNet("build -bl -nr:false");

        var binlog = Path.Combine(RootDirectory, "msbuild.binlog");
        var complog = Path.Combine(RootDirectory, "msbuild.complog");
        var result = CompilerLogUtil.TryConvertBinaryLog(binlog, complog);
        Assert.True(result.Succeeded);

        TestExport(
            compilerLogFilePath: complog,
            expectedCount: 1,
            excludeAnalyzers: true,
            runBuild: true,
            verifyBuildResult: static result =>
            {
                // warning CS2023: Ignoring /noconfig option because it was specified in a response file
                Assert.DoesNotContain("CS2023", result.StandardOut);
            });
    }

    [Fact]
    public void ExportWithErrorLog()
    {
        RunDotNet("new console --name example --output .");
        AddProjectProperty("<ErrorLog>my.example.sarif,version=1.0</ErrorLog>");
        RunDotNet("build -bl -nr:false");
        TestExport(1);
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

    [Theory]
    [MemberData(nameof(GetMissingFileArguments))]
    public void MissingFiles(string? option, string fileName, bool _)
    {
        var diagnostics = new List<string>();
        var filePath = Path.Combine(RootDirectory, fileName);
        var prefix = option is null ? "" : $"/{option}:";
        using var reader = ChangeCompilerCall(
            Fixture.Console.Value.BinaryLogPath!,
            x => x.ProjectFileName == "console.csproj",
            (c, a) => (c, [..a, $"{prefix}{filePath}"]),
            diagnostics: diagnostics);
        Assert.Equal([RoslynUtil.GetDiagnosticMissingFile(filePath)], diagnostics);

        using var writer = new StringWriter();
        var compilerCall = reader.ReadCompilerCall(0);
        ExportUtil.ExportRsp(reader.ReadArguments(compilerCall), writer);
        Assert.Contains(fileName, writer.ToString());

#if NET
        using var scratchDir = new TempDir("export test");
        var exportUtil = new ExportUtil(reader, excludeAnalyzers: false);
        exportUtil.ExportAll(scratchDir.DirectoryPath, SdkUtil.GetSdkCompilerDirectories());
#endif
    }

    [Fact]
    public void ContentBuilder_NormalizePathNull()
    {
        using var temp = new TempDir();
        var builder = new ExportUtil.ContentBuilder(
            destinationDirectory: temp.NewDirectory("dest"),
            originalSourceDirectory: temp.NewDirectory("src"),
            PathNormalizationUtil.Empty);
        Assert.Null(builder.NormalizePath(null));
    }

#if NET
    [Fact]
    public void ExportCrossPlatformLog()
    {
        var isWindows = OperatingSystem.IsWindows();
        var logData = isWindows ? Fixture.LinuxConsoleFromLog.Value : Fixture.WindowsConsoleFromLog.Value;
        TestExport(logData.CompilerLogPath, expectedCount: 1, runBuild: true, verifyExportCallback: tempPath =>
        {
            var allCsPaths = Directory.GetFiles(Path.Join(tempPath, "src"), "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(allCsPaths);
            foreach (var csPath in allCsPaths)
            {
                if (isWindows)
                {
                    Assert.DoesNotContain('/', csPath);
                }
                else
                {
                    Assert.DoesNotContain('\\', csPath);
                }
            }
        });
    }
#endif

    [Fact]
    public void ExportProjectBasic()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath);
        var exportUtil = new ExportUtil(reader, excludeAnalyzers: true);
        
        using var tempDir = new TempDir();
        exportUtil.ExportProject(tempDir.DirectoryPath);

        // Verify solution file exists
        var solutionFile = Path.Combine(tempDir.DirectoryPath, "export.slnx");
        Assert.True(File.Exists(solutionFile), "Solution file should exist");

        // Verify solution file format with complete content
        var solutionContent = File.ReadAllText(solutionFile);
        var expectedSolution = $$"""
            <Solution>
              <Project Path="console-{{TestUtil.TestTargetFramework}}/console-{{TestUtil.TestTargetFramework}}.csproj" />
            </Solution>
            """;
        Assert.Equal(expectedSolution, solutionContent.Trim());

        // Verify references directory exists
        var referencesDir = Path.Combine(tempDir.DirectoryPath, "references");
        Assert.True(Directory.Exists(referencesDir), "References directory should exist");

        // Verify project directory and files exist
        var projectDirs = Directory.GetDirectories(tempDir.DirectoryPath)
            .Where(d => !Path.GetFileName(d).Equals("references", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(projectDirs);

        var projectDir = projectDirs[0];
        var projectFiles = Directory.GetFiles(projectDir, "*.csproj");
        Assert.NotEmpty(projectFiles);

        var projectFile = projectFiles[0];
        var projectContent = File.ReadAllText(projectFile);
        
        // Verify complete project file content
        // The console project includes Program.cs and auto-generated files
        var expectedProject = $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>{{TestUtil.TestTargetFramework}}</TargetFramework>
                <AssemblyName>console</AssemblyName>
                <OutputType>Exe</OutputType>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
              </PropertyGroup>

              <ItemGroup>
                <Compile Include="Program.cs" />
                <Compile Include="console.GlobalUsings.g.cs" />
                <Compile Include=".NETCoreApp,Version=v9.0.AssemblyAttributes.cs" />
                <Compile Include="console.AssemblyInfo.cs" />
              </ItemGroup>

              <ItemGroup>
                <Reference Include="WindowsBase">
                  <HintPath>../references/WindowsBase.dll</HintPath>
                </Reference>
              </ItemGroup>

            </Project>
            """;
        Assert.Equal(expectedProject, projectContent.Trim());

        // Verify source files were copied
        var sourceFiles = Directory.GetFiles(projectDir, "*.cs");
        Assert.NotEmpty(sourceFiles);
    }

    [Fact]
    public void ExportProjectWithMultiTarget()
    {
        TestOutputHelper.WriteLine($"Testing multi-target project export from {Fixture.ClassLibMulti.Value.CompilerLogPath}");
        
        using var reader = CompilerLogReader.Create(Fixture.ClassLibMulti.Value.CompilerLogPath);
        var exportUtil = new ExportUtil(reader, excludeAnalyzers: true);
        
        using var tempDir = new TempDir();
        exportUtil.ExportProject(tempDir.DirectoryPath);

        // Verify solution file exists
        var solutionFile = Path.Combine(tempDir.DirectoryPath, "export.slnx");
        Assert.True(File.Exists(solutionFile), "Solution file should exist");

        var solutionContent = File.ReadAllText(solutionFile);
        TestOutputHelper.WriteLine("Solution content:");
        TestOutputHelper.WriteLine(solutionContent);

        // Verify complete solution content with both target frameworks
        var expectedSolution = """
            <Solution>
              <Project Path="classlibmulti-net6.0/classlibmulti-net6.0.csproj" />
              <Project Path="classlibmulti-net9.0/classlibmulti-net9.0.csproj" />
            </Solution>
            """;
        Assert.Equal(expectedSolution, solutionContent.Trim());
    }

    [Fact]
    public void ExportProjectValidateFrameworkReferencesFiltered()
    {
        using var reader = CompilerLogReader.Create(Fixture.Console.Value.CompilerLogPath);
        var exportUtil = new ExportUtil(reader, excludeAnalyzers: true);
        
        using var tempDir = new TempDir();
        exportUtil.ExportProject(tempDir.DirectoryPath);

        // Find the project file
        var projectFiles = Directory.GetFiles(tempDir.DirectoryPath, "*.csproj", SearchOption.AllDirectories);
        Assert.NotEmpty(projectFiles);

        var projectContent = File.ReadAllText(projectFiles[0]);
        
        // Verify complete project file content - framework references should be filtered out
        var expectedProject = $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>{{TestUtil.TestTargetFramework}}</TargetFramework>
                <AssemblyName>console</AssemblyName>
                <OutputType>Exe</OutputType>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
              </PropertyGroup>

              <ItemGroup>
                <Compile Include="Program.cs" />
                <Compile Include="console.GlobalUsings.g.cs" />
                <Compile Include=".NETCoreApp,Version=v9.0.AssemblyAttributes.cs" />
                <Compile Include="console.AssemblyInfo.cs" />
              </ItemGroup>

              <ItemGroup>
                <Reference Include="WindowsBase">
                  <HintPath>../references/WindowsBase.dll</HintPath>
                </Reference>
              </ItemGroup>

            </Project>
            """;
        Assert.Equal(expectedProject, projectContent.Trim());
    }
}
