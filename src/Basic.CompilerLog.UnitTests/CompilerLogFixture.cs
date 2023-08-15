using Basic.CompilerLog.Util;
using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
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
    internal string StorageDirectory { get; }
    
    /// <summary>
    /// Directory that holds the log files
    /// </summary>
    internal string ComplogDirectory { get; }

    internal string ConsoleComplogPath { get; }

    internal string ConsoleNoGeneratorComplogPath { get; }

    internal string ClassLibComplogPath { get; }

    internal string ClassLibSignedComplogPath { get; }

    /// <summary>
    /// A multi-targeted class library
    /// </summary>
    internal string ClassLibMultiComplogPath { get; }

    internal string? WpfAppComplogPath { get; }

    internal IEnumerable<string> AllComplogs { get; }

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
        void RunDotnetCommand(string args, string workingDirectory)
        {
            diagnosticBuilder.AppendLine($"Running: {args} in {workingDirectory}");
            var result = DotnetUtil.Command(args, workingDirectory);
            diagnosticBuilder.AppendLine($"Succeeded: {result.Succeeded}");
            diagnosticBuilder.AppendLine($"Standard Output: {result.StandardOut}");
            diagnosticBuilder.AppendLine($"Standard Error: {result.StandardError}");
            Assert.True(result.Succeeded);
        }

        var allCompLogs = new List<string>();
        ConsoleComplogPath = WithBuild("console.complog", string (string scratchPath) =>
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
            RunDotnetCommand("build -bl", scratchPath);
            return Path.Combine(scratchPath, "msbuild.binlog");
        });

        ConsoleNoGeneratorComplogPath = WithBuild("console-no-generator.complog", string (string scratchPath) =>
        {
            RunDotnetCommand($"new console --name example-no-generator --output .", scratchPath);
            RunDotnetCommand("build -bl", scratchPath);
            return Path.Combine(scratchPath, "msbuild.binlog");
        });
        
        ClassLibComplogPath = WithBuild("classlib.complog", string (string scratchPath) =>
        {
            RunDotnetCommand($"new classlib --name classlib --output .", scratchPath);
            var projectFileContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net7.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(scratchPath, "classlib.csproj"), projectFileContent, TestBase.DefaultEncoding);
            var program = """
                using System;
                using System.Text.RegularExpressions;

                partial class Util {
                    [GeneratedRegex("abc|def", RegexOptions.IgnoreCase, "en-US")]
                    internal static partial Regex GetRegex();
                }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Class1.cs"), program, TestBase.DefaultEncoding);
            RunDotnetCommand("build -bl", scratchPath);
            return Path.Combine(scratchPath, "msbuild.binlog");
        });

        ClassLibSignedComplogPath = WithBuild("classlibsigned.complog", string (string scratchPath) =>
        {
            RunDotnetCommand($"new classlib --name classlibsigned --output .", scratchPath);
            var keyFilePath = Path.Combine(scratchPath, "Key.snk");
            var projectFileContent = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net7.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <KeyOriginatorFile>{keyFilePath}</KeyOriginatorFile>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(scratchPath, "classlibsigned.csproj"), projectFileContent, TestBase.DefaultEncoding);
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
            RunDotnetCommand("build -bl", scratchPath);
            return Path.Combine(scratchPath, "msbuild.binlog");
        });

        ClassLibMultiComplogPath = WithBuild("classlibmulti.complog", string (string scratchPath) =>
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
            File.WriteAllText(Path.Combine(scratchPath, "Class 1.cs"), program, TestBase.DefaultEncoding);
            RunDotnetCommand("build -bl", scratchPath);
            return Path.Combine(scratchPath, "msbuild.binlog");
        });

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WpfAppComplogPath = WithBuild("wpfapp.complog", string (string scratchPath) =>
            {
                RunDotnetCommand("new wpf --name wpfapp --output .", scratchPath);
                RunDotnetCommand("build -bl", scratchPath);
                return Path.Combine(scratchPath, "msbuild.binlog");
            });
        }

        AllComplogs = allCompLogs;
        string WithBuild(string name, Func<string, string> action)
        {
            try
            {
                var scratchPath = Path.Combine(StorageDirectory, "scratch dir", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratchPath);
                RunDotnetCommand("new globaljson --sdk-version 7.0.400", scratchPath);
                var binlogFilePath = action(scratchPath);
                Assert.True(File.Exists(binlogFilePath));
                var complogFilePath = Path.Combine(ComplogDirectory, name);
                var diagnostics = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, complogFilePath);
                Assert.Empty(diagnostics);
                Directory.Delete(scratchPath, recursive: true);
                allCompLogs.Add(complogFilePath);
                return complogFilePath;
            }
            catch (Exception ex)
            {
                messageSink.OnMessage(new DiagnosticMessage(diagnosticBuilder.ToString()));
                throw new Exception($"Cannot generate compiler log {name}", ex);
            }
        }
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
