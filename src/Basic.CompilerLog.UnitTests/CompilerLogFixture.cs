using Basic.CompilerLog.Util;
using Microsoft.VisualStudio.TestPlatform.Common.DataCollection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class CompilerLogFixture : IDisposable
{
    internal string StorageDirectory { get; }
    internal string BuildDirectory { get; }
    internal string CompilerLogDirectory { get; }

    internal string ConsolePath { get; }
    internal string ClassLibPath { get; }

    internal IEnumerable<string> AllCompilerLogs => new[]
    {
        ConsolePath,
        ClassLibPath
    };

    public CompilerLogFixture()
    {
        StorageDirectory = Path.Combine(Path.GetTempPath(), nameof(CompilerLogFixture), Guid.NewGuid().ToString("N"));
        BuildDirectory = Path.Combine(StorageDirectory, "build");
        CompilerLogDirectory = Path.Combine(StorageDirectory, "compiler-logs");
        Directory.CreateDirectory(BuildDirectory);
        Directory.CreateDirectory(CompilerLogDirectory);

        ConsolePath = WithBuild("console.complog", () =>
        {
            DotnetUtil.Command($"new console --name example --output .", BuildDirectory);
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
            File.WriteAllText(Path.Combine(BuildDirectory, "example.csproj"), projectFileContent, TestBase.DefaultEncoding);
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
            File.WriteAllText(Path.Combine(BuildDirectory, "Program.cs"), program, TestBase.DefaultEncoding);
            DotnetUtil.Command("build -bl", BuildDirectory);
        });
        
        ClassLibPath = WithBuild("classlib.complog", () =>
        {
            DotnetUtil.Command($"new classlib --name example --output .", BuildDirectory);
            var projectFileContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net7.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(BuildDirectory, "example.csproj"), projectFileContent, TestBase.DefaultEncoding);
            var program = """
                using System;
                using System.Text.RegularExpressions;

                partial class Util {
                    [GeneratedRegex("abc|def", RegexOptions.IgnoreCase, "en-US")]
                    internal static partial Regex GetRegex();
                }
                """;
            File.WriteAllText(Path.Combine(BuildDirectory, "Class1.cs"), program, TestBase.DefaultEncoding);
            DotnetUtil.Command("build -bl", BuildDirectory);
        });

        string WithBuild(string name, Action action)
        {
            action();
            var filePath = Path.Combine(CompilerLogDirectory, name);
            var diagnostics = CompilerLogUtil.ConvertBinaryLog(Path.Combine(BuildDirectory, "msbuild.binlog"), filePath);
            Assert.Empty(diagnostics);
            Directory.Delete(BuildDirectory, recursive: true);
            Directory.CreateDirectory(BuildDirectory);
            return filePath;
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
