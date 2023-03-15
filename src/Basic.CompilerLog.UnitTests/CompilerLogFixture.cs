using Basic.CompilerLog.Util;
using Microsoft.Build.Framework;
using Microsoft.VisualStudio.TestPlatform.Common.DataCollection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class CompilerLogFixture : IDisposable
{
    internal string StorageDirectory { get; }
    
    /// <summary>
    /// Directory that holds the log files
    /// </summary>
    internal string ComplogDirectory { get; }

    internal string ConsoleComplogPath { get; }

    internal string ClassLibComplogPath { get; }

    internal IEnumerable<string> AllComplogs => new[]
    {
        ConsoleComplogPath,
        ClassLibComplogPath,
    };

    public CompilerLogFixture()
    {
        StorageDirectory = Path.Combine(Path.GetTempPath(), nameof(CompilerLogFixture), Guid.NewGuid().ToString("N"));
        ComplogDirectory = Path.Combine(StorageDirectory, "logs");
        Directory.CreateDirectory(ComplogDirectory);

        ConsoleComplogPath = WithBuild("console.complog", static string (string scratchPath) =>
        {
            DotnetUtil.Command($"new console --name example --output .", scratchPath);
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
            File.WriteAllText(Path.Combine(scratchPath, "example.csproj"), projectFileContent, TestBase.DefaultEncoding);
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
            DotnetUtil.Command("build -bl", scratchPath);
            return Path.Combine(scratchPath, "msbuild.binlog");
        });
        
        ClassLibComplogPath = WithBuild("classlib.complog", static string (string scratchPath) =>
        {
            DotnetUtil.Command($"new classlib --name example --output .", scratchPath);
            var projectFileContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net7.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(scratchPath, "example.csproj"), projectFileContent, TestBase.DefaultEncoding);
            var program = """
                using System;
                using System.Text.RegularExpressions;

                partial class Util {
                    [GeneratedRegex("abc|def", RegexOptions.IgnoreCase, "en-US")]
                    internal static partial Regex GetRegex();
                }
                """;
            File.WriteAllText(Path.Combine(scratchPath, "Class1.cs"), program, TestBase.DefaultEncoding);
            DotnetUtil.Command("build -bl", scratchPath);
            return Path.Combine(scratchPath, "msbuild.binlog");
        });

        string WithBuild(string name, Func<string, string> action)
        {
            var scratchPath = Path.Combine(StorageDirectory, "scratch");
            Directory.CreateDirectory(scratchPath);
            var binlogFilePath = action(scratchPath);
            var complogFilePath = Path.Combine(ComplogDirectory, name);
            var diagnostics = CompilerLogUtil.ConvertBinaryLog(binlogFilePath, complogFilePath);
            Assert.Empty(diagnostics);
            Directory.Delete(scratchPath, recursive: true);
            return complogFilePath;
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
