using Basic.CompilerLog.Util;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Xunit;
using Xunit.Sdk;

namespace Basic.CompilerLog.UnitTests;

public readonly struct LogData(string? projectFilePath, string compilerLogPath, string? binaryLogPath, bool supportsNoneHost = true)
{
    public string? ProjectFilePath { get; } = projectFilePath;
    public string CompilerLogPath { get; } = compilerLogPath;
    public string? BinaryLogPath { get; } = binaryLogPath;
    public bool SupportsNoneHost { get; } = supportsNoneHost;

    public override string ToString() => $"{Path.GetFileName(CompilerLogPath)}";
}

public sealed class FileLockHold(List<Stream> streams) : IDisposable
{
    public List<Stream> Streams { get; } = streams;

    public void Dispose()
    {
        foreach (var stream in Streams)
        {
            stream.Dispose();
        }
        Streams.Clear();
    }
}

public sealed class CompilerLogFixture : FixtureBase, IDisposable
{
    private List<ReadOnlyDirectoryScope> ReadOnlyDirectoryScopes { get; } = new();

    internal ImmutableArray<Lazy<LogData>> AllLogs { get; }

    /// <summary>
    /// Storage directory for all the generated artifacts and scratch directories
    /// </summary>
    internal string StorageDirectory { get; }

    internal string ScratchDirectory { get; }

    /// <summary>
    /// Directory that holds the log files
    /// </summary>
    internal string ComplogDirectory { get; }

    internal Lazy<LogData> Console { get; }

    internal Lazy<LogData> ConsoleNoGenerator { get; }

    /// <summary>
    /// A console project that has a reference to a library
    /// </summary>
    internal Lazy<LogData> ConsoleWithReference { get; }

    /// <summary>
    /// This is a solution that has two projects (library and console) where the console
    /// has a reference to the library using an extern alias.
    /// </summary>
    internal Lazy<LogData> ConsoleWithAliasReference { get; }

    /// <summary>
    /// This is a console project that has every nasty feature that can be thought of
    /// like resources, line directives, embeds, etc ... Rather than running a
    /// `dotnet build` for every one of these individually (which is expensive) in
    /// unit tests try to create a single project that has all of them.
    /// </summary>
    internal Lazy<LogData> ConsoleComplex { get; }

    internal Lazy<LogData> ClassLib { get; }

    internal Lazy<LogData> ClassLibRefOnly { get; }

    /// <summary>
    /// A multi-targeted class library
    /// </summary>
    internal Lazy<LogData> ClassLibMulti { get; }

    /// <summary>
    /// A class library that has resource dlls
    /// </summary>
    internal Lazy<LogData> ClassLibWithResourceLibs { get; }

    internal Lazy<LogData> ConsoleVisualBasic { get; }

    internal Lazy<LogData>? WpfApp { get; }

    internal Lazy<LogData>? WinFormsApp { get; }

    internal Lazy<LogData>? ConsoleWithNativePdb { get; }

    internal Lazy<LogData> LinuxConsoleFromLog { get; }

    internal Lazy<LogData> WindowsConsoleFromLog  { get; }

    /// <summary>
    /// Named complog value that makes intent of getting signed one clear
    /// </summary>
    internal Lazy<LogData> ConsoleSigned => ConsoleComplex;

    /// <summary>
    /// Constructor for the primary fixture. To get actual diagnostic messages into the output
    /// Add the following to xunit.runner.json to enable "diagnosticMessages": true
    /// </summary>
    public CompilerLogFixture(IMessageSink messageSink)
        : base(messageSink)
    {
        StorageDirectory = Path.Combine(TestUtil.TestTempRoot, "compilerlogfixture");
        ComplogDirectory = Path.Combine(StorageDirectory, "logs");
        ScratchDirectory = Path.Combine(StorageDirectory, "scratch dir");
        Directory.CreateDirectory(ComplogDirectory);
        Directory.CreateDirectory(ScratchDirectory);

        RunDotnetCommand($"new globaljson --sdk-version {TestUtil.SdkVersion} --roll-forward minor", ScratchDirectory);

        var testArtifactsDir = Path.Combine(TestUtil.TestArtifactsDirectory, "compilerlogfixture");
        Directory.CreateDirectory(testArtifactsDir);

        var builder = ImmutableArray.CreateBuilder<Lazy<LogData>>();
        Console = WithBuild("console.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new console --name console --output .", scratchPath);
            var projectFileContent = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>{TestUtil.TestTargetFramework}</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(scratchPath, "console.csproj"), projectFileContent, TestBase.DefaultEncoding);
            var program = """
                using System;
                using System.Text.RegularExpressions;
                // This is an amazing resource
                var r = Util.GetRegex();
                Console.WriteLine(r);

                partial class Util {
                    [GeneratedRegex("abc|def", RegexOptions.IgnoreCase, "en-US")]
                    internal static partial Regex GetRegex();
                }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Program.cs"), program, TestBase.DefaultEncoding);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });

        ClassLibMulti = WithBuild("classlibmulti.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new classlib --name classlibmulti --output .", scratchPath);
            var projectFileContent = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net6.0;{TestUtil.TestTargetFramework}</TargetFrameworks>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(scratchPath, "classlibmulti.csproj"), projectFileContent, TestBase.DefaultEncoding);
            var program = """
                using System;
                using System.Text.RegularExpressions;

                partial class Util {
                    internal static Regex GetRegex() => null!;
                }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Class1.cs"), program, TestBase.DefaultEncoding);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });

        ClassLibRefOnly = WithBuild("classlibrefonly.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new classlib --name classlibrefonly --output .", scratchPath);
            var projectFileContent = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net6.0;{TestUtil.TestTargetFramework}</TargetFrameworks>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <ProduceOnlyReferenceAssembly>true</ProduceOnlyReferenceAssembly>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(scratchPath, "classlibrefonly.csproj"), projectFileContent, TestBase.DefaultEncoding);
            var program = """
                public class Util {
                    public void M() { }
                }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Util.cs"), program, TestBase.DefaultEncoding);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        }, expectDiagnosticMessages: true);

        ClassLibWithResourceLibs = WithBuild("classlibwithresources.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new classlib --name classlibwithresources --output .", scratchPath);
            var resx = """
                <?xml version="1.0" encoding="utf-8"?>
                <root>
                <resheader name="resmimetype">
                    <value>text/microsoft-resx</value>
                </resheader>
                <resheader name="version">
                    <value>2.0</value>
                </resheader>
                <resheader name="reader">
                    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
                </resheader>
                <resheader name="writer">
                    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
                </resheader>
                <data name="String1" xml:space="preserve">
                    <value>Hello, World!</value>
                </data>
                <data name="String2" xml:space="preserve">
                    <value>Welcome to .NET</value>
                </data>
                </root>
                """;
            File.WriteAllText(Path.Combine(scratchPath, "strings.resx"), resx, TestBase.DefaultEncoding);
            File.WriteAllText(Path.Combine(scratchPath, "strings.de.resx"), resx, TestBase.DefaultEncoding);
            File.WriteAllText(Path.Combine(scratchPath, "strings.ko.resx"), resx, TestBase.DefaultEncoding);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });

        ConsoleNoGenerator = WithBuild("console-no-generator.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new console --name example-no-generator --output .", scratchPath);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });

        ConsoleVisualBasic = WithBuild("console-vb.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new console --name console-vb --language VB --output .", scratchPath);
            File.WriteAllText(Path.Combine(scratchPath, "Extra.vb"), """
                Module M

                #ExternalSource("line.txt", 0)
                    Sub G()

                    End Sub
                #End ExternalSource
                End Module
                """, TestBase.DefaultEncoding);
            File.WriteAllText(Path.Combine(scratchPath, "line.txt"), "this is content", TestBase.DefaultEncoding);
            File.WriteAllText(Path.Combine(scratchPath, "console-vb.vbproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                    <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <RootNamespace>vbconsole</RootNamespace>
                        <TargetFramework>{TestUtil.TestTargetFramework}</TargetFramework>
                        <EmbedAllSources>true</EmbedAllSources>
                    </PropertyGroup>
                </Project>
                """, TestBase.DefaultEncoding);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });

        ConsoleComplex = WithBuild("console-complex.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new console --name console-complex --output .", scratchPath);
            var keyFilePath = Path.Combine(scratchPath, "Key.snk");
            File.WriteAllText(Path.Combine(scratchPath, "console-complex.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>{TestUtil.TestTargetFramework}</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <DebugType>embedded</DebugType>
                    <EmbedAllSources>true</EmbedAllSources>
                    <CodeAnalysisRuleset>{scratchPath}\example.ruleset</CodeAnalysisRuleset>
                    <Win32Manifest>resource.txt</Win32Manifest>
                    <KeyOriginatorFile>{keyFilePath}</KeyOriginatorFile>
                    <DocumentationFile>console-complex.xml</DocumentationFile>
                    <SourceLink>example.sourcelink</SourceLink>
                    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
                  </PropertyGroup>
                  <ItemGroup>
                    <EmbeddedResource Include="resource.txt" />
                    <AdditionalFiles Include="additional.txt" FixtureKey="true" />
                    <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="FixtureKey" />
                  </ItemGroup>
                </Project>
                """, TestBase.DefaultEncoding);

            File.WriteAllBytes(keyFilePath, ResourceLoader.GetResourceBlob("Key.snk"));

            File.WriteAllText(Path.Combine(scratchPath, "Extra.cs"), """
                using System;
                using System.Text.RegularExpressions;

                // File that does exsit
                #line 42 "line.txt"
                class C { }
                """, TestBase.DefaultEncoding);
            File.WriteAllText(Path.Combine(scratchPath, "line.txt"), "this is content", TestBase.DefaultEncoding);

            File.WriteAllText(Path.Combine(scratchPath, "additional.txt"), """
                This is an additional file.
                It just has some text in it
                """, TestBase.DefaultEncoding);

            // File with a space in the name to make sure we quote correctly in RSP
            File.WriteAllText(Path.Combine(scratchPath, "Code With Space In The Name.cs"), """
                class D { }
                """, TestBase.DefaultEncoding);

            File.WriteAllText(Path.Combine(scratchPath, "resource.txt"), """
                This is an awesome resource
                """);

            File.WriteAllText(Path.Combine(scratchPath, "example.ruleset"), """
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
                    <Include Path="nested.ruleset" Action="Default" />
                </RuleSet>
                """);

            File.WriteAllText(Path.Combine(scratchPath, "nested.ruleset"), """
                <RuleSet Name="Rules for Hello World project" Description="These rules focus on critical issues for the Hello World app." ToolsVersion="10.0">
                    <Rules AnalyzerId="Microsoft.CodeQuality.Analyzers" RuleNamespace="Microsoft.CodeQuality.Analyzers">
                        <Rule Id="CA1802" Action="Error" />
                        <Rule Id="CA1814" Action="Info" />
                        <Rule Id="CA1823" Action="None" />
                        <Rule Id="CA2217" Action="Warning" />
                    </Rules>
                </RuleSet>
                """);

            File.WriteAllText(Path.Combine(scratchPath, "example.sourcelink"), """
                {
                    "documents": {
                        "C:\\src\\complog\\*": "https://raw.githubusercontent.com/dotnet/complog/bcc51178e1a82fb2edaf47285f6e577989a7333f/*"
                    }
                }
                """);

            File.WriteAllText(Path.Combine(scratchPath, ".editorconfig"), """
                # This file is the top-most EditorConfig file
                root = true

                # All Files
                [*]
                charset = utf-8
                indent_style = space
                indent_size = 4
                insert_final_newline = true
                trim_trailing_whitespace = true

                [*.{cs,vb,}]
                # Default Severity for all .NET Code Style rules below
                dotnet_analyzer_diagnostic.severity = warning

                dotnet_style_qualification_for_field = true:warning
                dotnet_style_qualification_for_property = true:warning
                dotnet_style_qualification_for_method = true:warning
                dotnet_style_qualification_for_event = true:warning
                """);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });

        ClassLib = WithBuild("classlib.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new classlib --name classlib --output . --framework net8.0", scratchPath);
            var program = """
                using System;
                using System.Text.RegularExpressions;

                partial class Util {
                    [GeneratedRegex("abc|def", RegexOptions.IgnoreCase, "en-US")]
                    internal static partial Regex GetRegex();
                }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Class1.cs"), program, TestBase.DefaultEncoding);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });

        ConsoleWithReference = WithBuild("console-with-project-ref.complog", void (string scratchPath) =>
        {
            RunDotnetCommand("new sln -n ConsoleWithProjectRef", scratchPath);

            // Create a class library for referencing
            var classLibPath = Path.Combine(scratchPath, "classlib");
            _ = Directory.CreateDirectory(classLibPath);
            RunDotnetCommand("new classlib -o . -n util", classLibPath);
            File.WriteAllText(
                Path.Combine(classLibPath, "Class1.cs"),
                """
                using System;
                namespace Util;
                public static class NameInfo
                {
                    public static string GetName() => "Hello World";
                }
                """,
                TestBase.DefaultEncoding);
            RunDotnetCommand($@"sln add ""{classLibPath}""", scratchPath);

            // Create a console project that references the class library
            var consolePath = Path.Combine(scratchPath, "console");
            _ = Directory.CreateDirectory(consolePath);
            RunDotnetCommand("new console -o . -n console-with-reference", consolePath);
            File.WriteAllText(
                Path.Combine(consolePath, "Program.cs"),
                """
                using System;
                using Util;
                Console.WriteLine(NameInfo.GetName());
                """,
                TestBase.DefaultEncoding);
            RunDotnetCommand($@"add . reference ""{classLibPath}""", consolePath);
            RunDotnetCommand($@"sln add ""{consolePath}""", scratchPath);

            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });

        ConsoleWithAliasReference = WithBuild("console-with-alias-project-ref.complog", void (string scratchPath) =>
        {
            RunDotnetCommand("new sln -n ConsoleWithProjectAliasRef", scratchPath);

            // Create a class library for referencing
            var classLibPath = Path.Combine(scratchPath, "classlib");
            _ = Directory.CreateDirectory(classLibPath);
            RunDotnetCommand("new classlib -o . -n util", classLibPath);
            File.WriteAllText(
                Path.Combine(classLibPath, "Class1.cs"),
                """
                using System;
                namespace Util;
                public static class NameInfo
                {
                    public static string GetName() => "Hello World";
                }
                """,
                TestBase.DefaultEncoding);
            RunDotnetCommand($@"sln add ""{classLibPath}""", scratchPath);

            // Create a console project that references the class library
            var consolePath = Path.Combine(scratchPath, "console");
            _ = Directory.CreateDirectory(consolePath);
            RunDotnetCommand("new console -o . -n console-with-alias-reference", consolePath);
            File.WriteAllText(
                Path.Combine(consolePath, "Program.cs"),
                """
                extern alias Util;
                using System;
                using Util;
                Console.WriteLine(Util::Util.NameInfo.GetName());
                """,
                TestBase.DefaultEncoding);
            RunDotnetCommand($@"add . reference ""{classLibPath}""", consolePath);

            var consoleProjectPath = Path.Combine(consolePath, "console-with-alias-reference.csproj");
            AddExternAlias(consoleProjectPath, "Util");
            RunDotnetCommand($@"sln add ""{consolePath}""", scratchPath);

            RunDotnetCommand("build -bl -nr:false", scratchPath);

            static void AddExternAlias(string projectFilePath, string aliasName)
            {
                var oldLines = File.ReadAllLines(projectFilePath);
                var newlines = new List<string>(capacity: oldLines.Length + 2);

                for (int i = 0; i < oldLines.Length; i++)
                {
                    var line = oldLines[i];
                    if (line.Contains("<ProjectReference", StringComparison.Ordinal))
                    {
                        newlines.Add(line.Replace("/>", ">"));
                        newlines.Add($"        <Aliases>{aliasName}</Aliases>");
                        newlines.Add($"    </ProjectReference>");
                    }
                    else
                    {
                        newlines.Add(line);
                    }
                }
                File.WriteAllLines(projectFilePath, newlines);
            }
        });

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WpfApp = WithBuild("wpfapp.complog", void (string scratchPath) =>
            {
                RunDotnetCommand("new wpf --name wpfapp --output .", scratchPath);
                RunDotnetCommand("build -bl -nr:false", scratchPath);
            });

            WinFormsApp = WithBuild("winformsapp.complog", void (string scratchPath) =>
            {
                RunDotnetCommand("new winforms --name winformsapp --output .", scratchPath);
                RunDotnetCommand("build -bl -nr:false", scratchPath);
            });

            ConsoleWithNativePdb= WithBuild("console-with-nativepdb.complog", void (string scratchPath) =>
            {
                RunDotnetCommand($"new console --name console --output .", scratchPath);
                var projectFileContent = $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <DebugType>full</DebugType>
                        <TargetFramework>{TestUtil.TestTargetFramework}</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                      </PropertyGroup>
                    </Project>
                    """;
                File.WriteAllText(Path.Combine(scratchPath, "console.csproj"), projectFileContent, TestBase.DefaultEncoding);
                var program = """
                    using System;
                    using System.Text.RegularExpressions;
                    // This is an amazing resource
                    var r = Util.GetRegex();
                    Console.WriteLine(r);

                    partial class Util {
                        [GeneratedRegex("abc|def", RegexOptions.IgnoreCase, "en-US")]
                        internal static partial Regex GetRegex();
                    }
                    """;
                File.WriteAllText(Path.Combine(scratchPath, "Program.cs"), program, TestBase.DefaultEncoding);
                RunDotnetCommand("build -bl -nr:false", scratchPath);
            }, supportsNoneHost: false);
        }

        LinuxConsoleFromLog = WithResource("linux-console.complog");
        WindowsConsoleFromLog = WithResource("windows-console.complog");

        AllLogs = builder.ToImmutable();

        // Make sure that the actual instance and static calculation of logs are in sync
        if (GetAllLogDataNames().Count() != AllLogs.Length)
        {
            throw new Exception($"The number of logs in the fixture ({AllLogs.Length}) does not match the number of logs in the test data ({GetAllLogDataNames().Count()})");
        }

        Lazy<LogData> WithBuild(string name, Action<string> action, bool expectDiagnosticMessages = false, bool supportsNoneHost = true)
        {
            var lazy = new Lazy<LogData>(() =>
            {
                var start = DateTime.UtcNow;
                try
                {
                    var scratchPath = Path.Combine(ScratchDirectory, Path.GetFileNameWithoutExtension(name));
                    Assert.False(Directory.Exists(scratchPath));
                    _ = Directory.CreateDirectory(scratchPath);
                    messageSink.OnDiagnosticMessage($"Starting {name} in {scratchPath}");
                    action(scratchPath);
                    var projectFilePath = Directory.EnumerateFiles(scratchPath, "*proj", SearchOption.TopDirectoryOnly).SingleOrDefault();
                    var binlogFilePath = Path.Combine(scratchPath, "msbuild.binlog");
                    Assert.True(File.Exists(binlogFilePath));
                    var complogFilePath = Path.Combine(ComplogDirectory, name);
                    var compilerCalls = new List<CompilerCall>();
                    var diagnostics = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, complogFilePath, cc =>
                    {
                        compilerCalls.Add(cc);
                        return true;
                    });

                    File.Copy(binlogFilePath, Path.Combine(testArtifactsDir, Path.ChangeExtension(name, ".binlog")), overwrite: !TestUtil.InGitHubActions);

                    if (!expectDiagnosticMessages)
                    {
                        Assert.Empty(diagnostics);
                    }

                    ReadOnlyDirectoryScopes.Add(new(scratchPath, setReadOnly: true));

                    return new LogData(projectFilePath, complogFilePath, binlogFilePath, supportsNoneHost);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Cannot generate compiler log {name}", ex);
                }
                finally
                {
                    messageSink.OnDiagnosticMessage($"Finished {name} {(DateTime.UtcNow - start).TotalSeconds:F2}s");
                }
            });

            builder.Add(lazy);
            return lazy;
        }

        Lazy<LogData> WithResource(string name)
        {
            var filePath = Path.Combine(ComplogDirectory, name);
            File.WriteAllBytes(filePath, ResourceLoader.GetResourceBlob(name));
            var lazy = new Lazy<LogData>(() => new LogData(null, filePath, null));
            builder.Add(lazy);
            return lazy;
        }
    }

    /// <summary>
    /// The underlying file system for these entries are read only by default. There are several tests though
    /// that need to be able to modify the files for tests. This method creates a copy of the log data in a
    /// read / write location for the test to use. It returns the path to the new project file
    /// </summary>
    /// <param name="logData"></param>
    /// <returns></returns>
    internal string GetWritableCopy(LogData logData, TempDir tempDir)
    {
        if (logData.ProjectFilePath is null)
        {
            throw new InvalidOperationException();
        }

        var directory = tempDir.CopyDirectory(Path.GetDirectoryName(logData.ProjectFilePath)!);
        return Path.Combine(directory, Path.GetFileName(logData.ProjectFilePath));
    }

    private async ValueTask<LogData> GetLogDataAsync(Lazy<LogData> lazyLogData, ITestOutputHelper testOutputHelper)
    {
        LogData logData;
        if (lazyLogData.IsValueCreated)
        {
            testOutputHelper.WriteLine($"Using cached value");
            logData = lazyLogData.Value;
        }
        else
        {
            TaskCompletionSource<LogData> tcs = new();
            await Task.Factory.StartNew(() =>
            {
                try
                {
                    var start = DateTime.UtcNow;
                    testOutputHelper.WriteLine($"Starting {nameof(GetAllCompilerLogs)}");
                    tcs.SetResult(lazyLogData.Value);
                    testOutputHelper.WriteLine($"Finished {nameof(GetAllCompilerLogs)} {(DateTime.UtcNow - start).TotalSeconds:F2}s");
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, TaskCreationOptions.LongRunning);
            logData = await tcs.Task;
        }

        return logData;
    }

    public async IAsyncEnumerable<LogData> GetAllLogData(ITestOutputHelper testOutputHelper)
    {
        var start = DateTime.UtcNow;
        foreach (var lazyLogData in AllLogs)
        {
            var logData = await GetLogDataAsync(lazyLogData, testOutputHelper);
            yield return logData;
        }
    }

    public async IAsyncEnumerable<LogData> GetAllLogData(ITestOutputHelper testOutputHelper, BasicAnalyzerKind? kind = null)
    {
        await foreach (var logData in GetAllLogData(testOutputHelper))
        {
            if (kind is not BasicAnalyzerKind.None || logData.SupportsNoneHost)
            {
                yield return logData;
            }
        }
    }

    /// <summary>
    /// This locks all the files on disk that are a part of this build. That allows us
    /// to validate operations involving the compiler log don't actually use the contents
    /// on disk
    /// </summary>
    public FileLockHold LockScratchDirectory()
    {
        var list = new List<Stream>();
        foreach (var filePath in Directory.EnumerateFiles(ScratchDirectory, "*", SearchOption.AllDirectories))
        {
            // Don't lock the binlog or complogs as that is what the code is actually going to be reading
            if (Path.GetExtension(filePath) is ".binlog" or ".complog")
            {
                continue;
            }

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            list.Add(stream);
        }
        return new FileLockHold(list);
    }

    public async IAsyncEnumerable<string> GetAllLogs(ITestOutputHelper testOutputHelper, BasicAnalyzerKind? kind = null)
    {
        await foreach (var logData in GetAllLogData(testOutputHelper, kind))
        {
            yield return logData.CompilerLogPath;
            if (logData.BinaryLogPath is { } binaryLogPath)
            {
                yield return binaryLogPath;
            }
        }
    }

    public async IAsyncEnumerable<string> GetAllCompilerLogs(ITestOutputHelper testOutputHelper, BasicAnalyzerKind? kind = null)
    {
        await foreach (var logData in GetAllLogData(testOutputHelper, kind))
        {
            yield return logData.CompilerLogPath;
        }
    }

    public async IAsyncEnumerable<string> GetAllBinaryLogs(ITestOutputHelper testOutputHelper, BasicAnalyzerKind? kind = null)
    {
        await foreach (var logData in GetAllLogData(testOutputHelper, kind))
        {
            if (logData.BinaryLogPath is { } binaryLog)
            {
                yield return binaryLog;
            }
        }
    }

    public void Dispose()
    {
        foreach (var scope in ReadOnlyDirectoryScopes)
        {
            scope.ClearReadOnly();
        }

        Directory.Delete(StorageDirectory, recursive: true);
    }

    /// <summary>
    /// This returns the names of all the log data files. This is used in theories to help break up
    /// the test cases into smaller pieces.
    /// </summary>
    public static IEnumerable<string> GetAllLogDataNames()
    {
        yield return nameof(Console);
        yield return nameof(ConsoleNoGenerator);
        yield return nameof(ConsoleWithReference);
        yield return nameof(ConsoleWithAliasReference);
        yield return nameof(ConsoleComplex);
        yield return nameof(ClassLib);
        yield return nameof(ClassLibRefOnly);
        yield return nameof(ClassLibMulti);
        yield return nameof(ClassLibWithResourceLibs);
        yield return nameof(ConsoleVisualBasic);
        yield return nameof(LinuxConsoleFromLog);
        yield return nameof(WindowsConsoleFromLog);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return nameof(WpfApp);
            yield return nameof(WinFormsApp);
            yield return nameof(ConsoleWithNativePdb);
        }
    }

    public Lazy<LogData> GetLogDataByName(string name)
    {
        var propertyInfo = GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (propertyInfo is null ||
            propertyInfo.GetValue(this) is not Lazy<LogData> logData)
        {
            throw new ArgumentException($"Cannot find valid {nameof(LogData)} for {name}");
        }

        return logData;
    }

    public async ValueTask<LogData> GetLogDataByNameAsync(string name, ITestOutputHelper testOutputHelper)
    {
        var logData = GetLogDataByName(name);
        return await GetLogDataAsync(logData, testOutputHelper);
    }
}

[CollectionDefinition(Name)]
public sealed class CompilerLogCollection : ICollectionFixture<CompilerLogFixture>
{
    public const string Name = "Compiler Log Collection";
}
