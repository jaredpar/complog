
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Sdk;

namespace Basic.CompilerLog.UnitTests;

/// <summary>
/// This fixture houses a solution with a variety of projects that have been built and
/// contain an available binary log.
/// </summary>
public sealed class SolutionFixture : FixtureBase, IDisposable
{
    internal ImmutableArray<string> ProjectPaths { get; }

    /// <summary>
    /// Storage directory for all the generated artifacts and scatch directories
    /// </summary>
    internal string StorageDirectory { get; }

    internal string SolutionPath { get; }

    internal string SolutionBinaryLogPath { get; }

    internal string ConsoleProjectPath { get; }

    internal string ConsoleProjectName => Path.GetFileName(ConsoleProjectPath);

    internal string ClassLibProjectPath { get; }

    internal string ClassLibMultiProjectPath { get; }

    /// <summary>
    /// A class library that has resource dlls
    /// </summary>
    internal string ClassLibWithResourceLibs { get; }

    internal string ConsoleWithDiagnosticsBinaryLogPath { get; }

    internal string ConsoleWithDiagnosticsProjectPath { get; }

    internal string? WpfAppProjectPath { get; }

    internal string ConsoleWithDiagnosticsProjectName => Path.GetFileName(ConsoleWithDiagnosticsProjectPath);

    /// <summary>
    /// The binary log for a project that has been removed from disk
    /// </summary>
    internal string RemovedBinaryLogPath { get; }

    /// <summary>
    /// This project is deleted off of disk after the binary log is created. This means subsequent calls
    /// to create a compiler log over it will fail. Useful for testing error cases.
    /// </summary>
    internal string RemovedConsoleProjectPath { get; }

    internal string RemovedConsoleProjectName => Path.GetFileName(RemovedConsoleProjectPath);

    public SolutionFixture(IMessageSink messageSink)
        : base(messageSink)
    {
        StorageDirectory = Path.Combine(TestUtil.TestTempRoot, "solutionlogfixture");
        Directory.CreateDirectory(StorageDirectory);
        SolutionPath = Path.Combine(StorageDirectory, "Solution.sln");
        var binlogDir = Path.Combine(StorageDirectory, "binlogs");
        Directory.CreateDirectory(binlogDir);

        RunDotnetCommand($"new globaljson --sdk-version {TestUtil.SdkVersion} --roll-forward minor", StorageDirectory);
        RunDotnetCommand("dotnet new sln -n Solution", StorageDirectory);

        var builder = ImmutableArray.CreateBuilder<string>();

        ConsoleProjectPath = WithProject("console", string (string dir) =>
        {
            RunDotnetCommand($"new console --name console -o . --framework {TestUtil.TestTargetFramework}", dir);
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
            File.WriteAllText(Path.Combine(dir, "Program.cs"), program, TestBase.DefaultEncoding);
            return Path.Combine(dir, "console.csproj");
        });

        ClassLibProjectPath = WithProject("classlib", string (string dir) =>
        {
            RunDotnetCommand($"new classlib --name classlib --framework {TestUtil.TestTargetFramework} -o .", dir);
            return Path.Combine(dir, "classlib.csproj");
        });

        ClassLibMultiProjectPath = WithProject("classlibmulti", string (string dir) =>
        {
            RunDotnetCommand("new classlib --name classlibmulti -o .", dir);
            var projectFileContent = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net6.0;{TestUtil.TestTargetFramework}</TargetFrameworks>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(dir, "classlibmulti.csproj"), projectFileContent, TestBase.DefaultEncoding);
            return Path.Combine(dir, "classlibmulti.csproj");
        });

        ClassLibWithResourceLibs = WithProject("classlibwithresources", string (string dir) =>
        {
            RunDotnetCommand($"new classlib --name classlibwithresources --output .", dir);
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
            File.WriteAllText(Path.Combine(dir, "strings.resx"), resx, TestBase.DefaultEncoding);
            File.WriteAllText(Path.Combine(dir, "strings.de.resx"), resx, TestBase.DefaultEncoding);
            File.WriteAllText(Path.Combine(dir, "strings.ko.resx"), resx, TestBase.DefaultEncoding);
            return Path.Combine(dir, "classlibwithresources.csproj");
        });

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WpfAppProjectPath = WithProject("wpfapp", string (string dir) =>
            {
                RunDotnetCommand("new wpf --name wpfapp2 -o .", dir);
                return Path.Combine(dir, "wpfapp2.csproj");
            });
        }

        string WithProject(string name, Func<string, string> func)
        {
            var dir = Path.Combine(StorageDirectory, name);
            Directory.CreateDirectory(dir);
            var projectPath = func(dir);
            RunDotnetCommand($@"dotnet sln add ""{projectPath}""", StorageDirectory);
            builder.Add(projectPath);
            return projectPath;
        };

        ProjectPaths = builder.ToImmutableArray();
        SolutionBinaryLogPath = Path.Combine(binlogDir, "msbuild.binlog");
        RunDotnetCommand($"build -bl:{SolutionBinaryLogPath} -nr:false", StorageDirectory);

        (RemovedConsoleProjectPath, RemovedBinaryLogPath) = CreateRemovedProject();
        (ConsoleWithDiagnosticsProjectPath, ConsoleWithDiagnosticsBinaryLogPath) = CreateConsoleWithDiagnosticsProject();

        (string, string) CreateRemovedProject()
        {
            var dir = Path.Combine(StorageDirectory, "removed");
            Directory.CreateDirectory(dir);
            RunDotnetCommand("new console --name removed-console -o .", dir);
            var projectPath = Path.Combine(dir, "removed-console.csproj");
            var binlogFilePath = Path.Combine(binlogDir, "removed-console.binlog");

            RunDotnetCommand($"build -bl:{binlogFilePath} -nr:false", dir);
            Directory.Delete(dir, recursive: true);
            return (projectPath, binlogFilePath);
        }

        (string, string) CreateConsoleWithDiagnosticsProject()
        {
            var dir = Path.Combine(StorageDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            RunDotnetCommand("new console --name console-with-diagnostics -o .", dir);
            File.WriteAllText(Path.Combine(dir, "Diagnostic.cs"), """
                using System;
                class C
                {
                    void Method1()
                    {
                        // Warning CS0219
                        int i = 42;
                    }

                    void Method2()
                    {
                        // Error CS0029
                        string s = 13;
                        Console.WriteLine(s);
                    }
                }
                """, TestBase.DefaultEncoding);
            var projectPath = Path.Combine(dir, "console-with-diagnostics.csproj");
            var binlogFilePath = Path.Combine(binlogDir, "console-with-diagnostics.binlog");
            var result = DotnetUtil.Command($"dotnet build -bl:{binlogFilePath} -nr:false", dir);
            Assert.False(result.Succeeded);
            return (projectPath, binlogFilePath);
        };
    }

    public void Dispose()
    {
        Directory.Delete(StorageDirectory, recursive: true);
    }
}

[CollectionDefinition(Name)]
public sealed class SolutionFixtureCollection : ICollectionFixture<SolutionFixture>
{
    public const string Name = "Solution Collection";
}
