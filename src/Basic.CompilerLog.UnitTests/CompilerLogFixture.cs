using Basic.CompilerLog.Util;
using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Basic.CompilerLog.UnitTests;

public sealed class CompilerLogFixture : IDisposable
{
    private readonly ImmutableArray<Lazy<string>> _allCompLogs;

    /// <summary>
    /// Storage directory for all the generated artifacts and scatch directories
    /// </summary>
    internal string StorageDirectory { get; }

    /// <summary>
    /// Directory that holds the log files
    /// </summary>
    internal string ComplogDirectory { get; }

    internal Lazy<string> ConsoleComplogPath { get; }

    internal Lazy<string> ConsoleNoGeneratorComplogPath { get; }

    /// <summary>
    /// This is a console project that has every nasty feature that can be thought of
    /// like resources, line directives, embeds, etc ... Rather than running a 
    /// `dotnet build` for every one of these individually (which is expensive) in 
    /// unit tests try to create a single project that has all of them.
    /// </summary>
    internal Lazy<string> ConsoleComplexComplogPath { get; }

    internal Lazy<string> ClassLibComplogPath { get; }

    /// <summary>
    /// A multi-targeted class library
    /// </summary>
    internal Lazy<string> ClassLibMultiComplogPath { get; }

    internal Lazy<string>? WpfAppComplogPath { get; }

    /// <summary>
    /// Named complog value that makes intent of getting signed one clear
    /// </summary>
    internal Lazy<string> ConsoleSignedComplogPath => ConsoleComplexComplogPath;

    /// <summary>
    /// Constructor for the primary fixture. To get actual diagnostic messages into the output 
    /// Add the following to xunit.runner.json to enable "diagnosticMessages": true
    /// </summary>
    public CompilerLogFixture(IMessageSink messageSink)
    {
        StorageDirectory = Path.Combine(Path.GetTempPath(), nameof(CompilerLogFixture), Guid.NewGuid().ToString("N"));
        ComplogDirectory = Path.Combine(StorageDirectory, "logs");
        Directory.CreateDirectory(ComplogDirectory);

        var testArtifactsDir = Environment.GetEnvironmentVariable("TEST_ARTIFACTS_DIR");
        if (testArtifactsDir is not null)
        {
            Directory.CreateDirectory(testArtifactsDir);
        }

        int processCount = 0;
        void RunDotnetCommand(string args, string workingDirectory)
        {
            (string, string)[]? extraEnvironmentVariables = null;
            if (testArtifactsDir is not null)
            {
                extraEnvironmentVariables = new (string, string)[]
                {
                    ("COREHOST_TRACE", "1"),
                    ("COREHOST_TRACEFILE", Path.Combine(testArtifactsDir, $"process.{processCount}.corehost.trace"))
                };
            }
            var start = DateTime.UtcNow;
            var diagnosticBuilder = new StringBuilder();

            diagnosticBuilder.AppendLine($"Running: {processCount++} {args} in {workingDirectory}");
            var result = DotnetUtil.Command(args, workingDirectory, extraEnvironmentVariables);
            diagnosticBuilder.AppendLine($"Succeeded: {result.Succeeded}");
            diagnosticBuilder.AppendLine($"Standard Output: {result.StandardOut}");
            diagnosticBuilder.AppendLine($"Standard Error: {result.StandardError}");
            diagnosticBuilder.AppendLine($"Finished: {(DateTime.UtcNow - start).TotalSeconds:F2}s");
            messageSink.OnMessage(new DiagnosticMessage(diagnosticBuilder.ToString()));
            Assert.True(result.Succeeded);
        }

        var builder = ImmutableArray.CreateBuilder<Lazy<string>>();
        ConsoleComplogPath = WithBuild("console.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new console --name console --output .", scratchPath);
            var projectFileContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net7.0</TargetFramework>
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

        ClassLibMultiComplogPath = WithBuild("classlibmulti.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new classlib --name classlibmulti --output .", scratchPath);
            var projectFileContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net6.0;net7.0</TargetFrameworks>
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


        ConsoleNoGeneratorComplogPath = WithBuild("console-no-generator.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new console --name example-no-generator --output .", scratchPath);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });
        
        ConsoleComplexComplogPath = WithBuild("console-complex.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new console --name console-complex --output .", scratchPath);
            var keyFilePath = Path.Combine(scratchPath, "Key.snk");
            File.WriteAllText(Path.Combine(scratchPath, "console-complex.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net7.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <EmbedAllSources>true</EmbedAllSources>
                    <CodeAnalysisRuleset>example.ruleset</CodeAnalysisRuleset>
                    <Win32Manifest>resource.txt</Win32Manifest>
                    <KeyOriginatorFile>{keyFilePath}</KeyOriginatorFile>
                  </PropertyGroup>
                  <ItemGroup>
                    <EmbeddedResource Include="resource.txt" />
                  </ItemGroup>
                </Project>
                """, TestBase.DefaultEncoding);

            File.WriteAllBytes(keyFilePath, ResourceLoader.GetResourceBlob("Key.snk"));

            File.WriteAllText(Path.Combine(scratchPath, "Extra.cs"), """
                using System;
                using System.Text.RegularExpressions;

                // File that does not exsit
                #line 42 "line.txt"
                class C { }
                """, TestBase.DefaultEncoding);
            File.WriteAllText(Path.Combine(scratchPath, "line.txt"), "this is content", TestBase.DefaultEncoding);

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
                    <Rules AnalyzerId="Microsoft.CodeQuality.Analyzers" RuleNamespace="Microsoft.CodeQuality.Analyzers">
                        <Rule Id="CA1802" Action="Error" />
                        <Rule Id="CA1814" Action="Info" />
                        <Rule Id="CA1823" Action="None" />
                        <Rule Id="CA2217" Action="Warning" />
                    </Rules>
                </RuleSet>
                """);
            RunDotnetCommand("build -bl -nr:false", scratchPath);
        });

        ClassLibComplogPath = WithBuild("classlib.complog", void (string scratchPath) =>
        {
            RunDotnetCommand($"new classlib --name classlib --output . --framework net7.0", scratchPath);
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WpfAppComplogPath = WithBuild("wpfapp.complog", void (string scratchPath) =>
            {
                RunDotnetCommand("new wpf --name wpfapp --output .", scratchPath);
                RunDotnetCommand("build -bl -nr:false", scratchPath);
            });
        }

        _allCompLogs = builder.ToImmutable();
        Lazy<string> WithBuild(string name, Action<string> action)
        {
            var lazy = new Lazy<string>(() =>
            {
                var start = DateTime.UtcNow;
                try
                {
                    var scratchPath = Path.Combine(StorageDirectory, "scratch dir", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(scratchPath);
                    messageSink.OnDiagnosticMessage($"Starting {name} in {scratchPath}");
                    RunDotnetCommand("new globaljson --sdk-version 7.0.400", scratchPath);
                    action(scratchPath);
                    var binlogFilePath = Path.Combine(scratchPath, "msbuild.binlog");
                    Assert.True(File.Exists(binlogFilePath));
                    var complogFilePath = Path.Combine(ComplogDirectory, name);
                    var diagnostics = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, complogFilePath);

                    if (testArtifactsDir is not null)
                    {
                        File.Copy(binlogFilePath, Path.Combine(testArtifactsDir, Path.ChangeExtension(name, ".binlog")));
                    }

                    Assert.Empty(diagnostics);
                    Directory.Delete(scratchPath, recursive: true);
                    return complogFilePath;
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
    }

    public IEnumerable<string> GetAllCompilerLogs(ITestOutputHelper testOutputHelper)
    {
        var start = DateTime.UtcNow;
        testOutputHelper.WriteLine($"Starting {nameof(GetAllCompilerLogs)}");
        var list = new List<string>(_allCompLogs.Length);
        foreach (var complog in _allCompLogs)
        {
            list.Add(complog.Value);
        }
        testOutputHelper.WriteLine($"Finished {nameof(GetAllCompilerLogs)} {(DateTime.UtcNow - start).TotalSeconds:F2}s");
        return list;
    } 

    public void Dispose()
    {
        Directory.Delete(StorageDirectory, recursive: true);
    }
}

[CollectionDefinition(Name)]
public sealed class CompilerLogCollection : ICollectionFixture<CompilerLogFixture>
{
    public const string Name = "Compiler Log Collection";
}
