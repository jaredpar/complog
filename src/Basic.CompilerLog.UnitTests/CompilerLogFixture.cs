using Basic.CompilerLog.Util;
using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private readonly ImmutableArray<Lazy<Task<string>>> _allCompLogs;

    /// <summary>
    /// Storage directory for all the generated artifacts and scatch directories
    /// </summary>
    internal string StorageDirectory { get; }

    /// <summary>
    /// Directory that holds the log files
    /// </summary>
    internal string ComplogDirectory { get; }

    internal Lazy<Task<string>> ConsoleComplogPath { get; }

    internal Lazy<Task<string>> ConsoleNoGeneratorComplogPath { get; }

    internal Lazy<Task<string>> ConsoleWithLineComplogPath { get; }

    internal Lazy<Task<string>> ConsoleWithLineAndEmbedComplogPath { get; }

    internal Lazy<Task<string>> ClassLibComplogPath { get; }

    internal Lazy<Task<string>> ClassLibSignedComplogPath { get; }

    /// <summary>
    /// A multi-targeted class library
    /// </summary>
    internal Lazy<Task<string>> ClassLibMultiComplogPath { get; }

    internal Lazy<Task<string>>? WpfAppComplogPath { get; }

    /// <summary>
    /// Constructor for the primary fixture. To get actual diagnostic messages into the output 
    /// Add the following to xunit.runner.json to enable "diagnosticMessages": true
    /// </summary>
    public CompilerLogFixture(IMessageSink messageSink)
    {
        StorageDirectory = Path.Combine(Path.GetTempPath(), nameof(CompilerLogFixture), Guid.NewGuid().ToString("N"));
        ComplogDirectory = Path.Combine(StorageDirectory, "logs");
        Directory.CreateDirectory(ComplogDirectory);

        var diagnosticBuilder = new StringBuilder();
        async Task RunDotnetCommand(string args, string workingDirectory)
        {
            diagnosticBuilder.AppendLine($"Running: {args} in {workingDirectory}");
            var result = await DotnetUtil.CommandAsync(args, workingDirectory);
            diagnosticBuilder.AppendLine($"Succeeded: {result.Succeeded}");
            diagnosticBuilder.AppendLine($"Standard Output: {result.StandardOut}");
            diagnosticBuilder.AppendLine($"Standard Error: {result.StandardError}");
            Assert.True(result.Succeeded);
        }

        var builder = ImmutableArray.CreateBuilder<Lazy<Task<string>>>();
        ConsoleComplogPath = WithBuild("console.complog", async Task (string scratchPath) =>
        {
            await RunDotnetCommand($"new console --name console --output .", scratchPath);
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
            await RunDotnetCommand("build -bl", scratchPath);
        });

        ConsoleNoGeneratorComplogPath = WithBuild("console-no-generator.complog", async Task (string scratchPath) =>
        {
            await RunDotnetCommand($"new console --name example-no-generator --output .", scratchPath);
            await RunDotnetCommand("build -bl", scratchPath);
        });
        
        ConsoleWithLineComplogPath = WithBuild("console-with-line.complog", async Task (string scratchPath) =>
        {
            await RunDotnetCommand($"new console --name console --output .", scratchPath);
            var extra = """
                using System;
                using System.Text.RegularExpressions;

                // File that does not exsit
                #line 42 "blah.txt"
                class C { }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Extra.cs"), extra, TestBase.DefaultEncoding);
            await RunDotnetCommand("build -bl", scratchPath);
        });

        ConsoleWithLineAndEmbedComplogPath = WithBuild("console-with-line-and-embed.complog", async Task (string scratchPath) =>
        {
            await RunDotnetCommand($"new console --name console --output .", scratchPath);
            DotnetUtil.AddProjectProperty("<EmbedAllSources>true</EmbedAllSources>", scratchPath);
            var extra = """
                using System;
                using System.Text.RegularExpressions;

                // File that does not exsit
                #line 42 "line.txt"
                class C { }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Extra.cs"), extra, TestBase.DefaultEncoding);
            File.WriteAllText(Path.Combine(scratchPath, "line.txt"), "this is content", TestBase.DefaultEncoding);
            await RunDotnetCommand("build -bl", scratchPath);
        });

        ClassLibComplogPath = WithBuild("classlib.complog", async Task (string scratchPath) =>
        {
            await RunDotnetCommand($"new classlib --name classlib --output . --framework net7.0", scratchPath);
            var program = """
                using System;
                using System.Text.RegularExpressions;

                partial class Util {
                    [GeneratedRegex("abc|def", RegexOptions.IgnoreCase, "en-US")]
                    internal static partial Regex GetRegex();
                }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Class1.cs"), program, TestBase.DefaultEncoding);
            await RunDotnetCommand("build -bl", scratchPath);
        });

        ClassLibSignedComplogPath = WithBuild("classlibsigned.complog", async Task (string scratchPath) =>
        {
            await RunDotnetCommand($"new classlib --name classlibsigned --output .", scratchPath);
            var keyFilePath = Path.Combine(scratchPath, "Key.snk");
            DotnetUtil.AddProjectProperty($"<KeyOriginatorFile>{keyFilePath}</KeyOriginatorFile>", scratchPath);
            File.WriteAllBytes(keyFilePath, ResourceLoader.GetResourceBlob("Key.snk"));
            var program = """
                using System;
                using System.Text.RegularExpressions;

                partial class Util {
                    [GeneratedRegex("abc|def", RegexOptions.IgnoreCase, "en-US")]
                    internal static partial Regex GetRegex();
                }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Class1.cs"), program, TestBase.DefaultEncoding);
            await RunDotnetCommand("build -bl", scratchPath);
        });

        ClassLibMultiComplogPath = WithBuild("classlibmulti.complog", async Task (string scratchPath) =>
        {
            await RunDotnetCommand($"new classlib --name classlibmulti --output .", scratchPath);
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
            await RunDotnetCommand("build -bl", scratchPath);
        });

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WpfAppComplogPath = WithBuild("wpfapp.complog", async Task (string scratchPath) =>
            {
                await RunDotnetCommand("new wpf --name wpfapp --output .", scratchPath);
                await RunDotnetCommand("build -bl", scratchPath);
            });
        }

        _allCompLogs = builder.ToImmutable();
        Lazy<Task<string>> WithBuild(string name, Func<string, Task> action)
        {
            var func = async Task<string> () =>
            {
                var start = DateTime.UtcNow;
                try
                {
                    var scratchPath = Path.Combine(StorageDirectory, "scratch dir", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(scratchPath);
                    messageSink.OnDiagnosticMessage($"Starting {name} in {scratchPath}");
                    await RunDotnetCommand("new globaljson --sdk-version 7.0.400", scratchPath);
                    await action(scratchPath);
                    var binlogFilePath = Path.Combine(scratchPath, "msbuild.binlog");
                    Assert.True(File.Exists(binlogFilePath));
                    var complogFilePath = Path.Combine(ComplogDirectory, name);
                    var diagnostics = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, complogFilePath);
                    Assert.Empty(diagnostics);
                    Directory.Delete(scratchPath, recursive: true);
                    return complogFilePath;
                }
                catch (Exception ex)
                {
                    diagnosticBuilder.Append($"Exception: {ex.Message}");
                    messageSink.OnDiagnosticMessage(diagnosticBuilder.ToString());
                    throw;
                }
                finally
                {
                    messageSink.OnDiagnosticMessage($"Finished {name} {(DateTime.UtcNow - start).TotalSeconds}");
                }
            };

            var lazy = new Lazy<Task<string>>(func, isThreadSafe: false);
            builder.Add(lazy);
            return lazy;
        }
    }

    public IEnumerable<Task<string>> GetAllCompLogs() => _allCompLogs.Select(x => x.Value);

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
