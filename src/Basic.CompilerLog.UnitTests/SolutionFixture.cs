
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
/// <remarks>
/// This fixture is registered as an assembly fixture and is therefore shared across test
/// classes that execute in parallel. The contents are built once in the constructor and are
/// read-only thereafter, so any mutable state added here must be thread-safe.
///
/// The dotnet new / build invocations that produce the solution are cached across test runs on
/// the machine via <see cref="FixtureBuildCache"/>.
/// </remarks>
public sealed class SolutionFixture : FixtureBase, IDisposable
{
    private ReadOnlyDirectoryScope ReadOnlyDirectoryScope { get; }

    /// <summary>
    /// When non-null the expensive dotnet builds are cached across test runs on this machine.
    /// </summary>
    private FixtureBuildCache? BuildCache { get; }

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

    internal string? WpfAppProjectPath { get; }

    public SolutionFixture(IMessageSink messageSink)
        : base(messageSink)
    {
        BuildCache = FixtureBuildCache.Instance;
        StorageDirectory = BuildCache is { } buildCache
            ? Path.Combine(buildCache.CacheDirectory, "solutionlogfixture")
            : Path.Combine(TestUtil.TestTempRoot, "solutionlogfixture");
        SolutionPath = Path.Combine(StorageDirectory, "Solution.sln");
        var binlogDir = Path.Combine(StorageDirectory, "binlogs");
        SolutionBinaryLogPath = Path.Combine(binlogDir, "msbuild.binlog");

        // The project paths are deterministic so they can be computed up front. The builds that
        // produce them only run when there is no cached copy from a previous test run.
        var builder = ImmutableArray.CreateBuilder<string>();
        ConsoleProjectPath = AddProjectPath("console", "console.csproj");
        ClassLibProjectPath = AddProjectPath("classlib", "classlib.csproj");
        ClassLibMultiProjectPath = AddProjectPath("classlibmulti", "classlibmulti.csproj");
        ClassLibWithResourceLibs = AddProjectPath("classlibwithresources", "classlibwithresources.csproj");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WpfAppProjectPath = AddProjectPath("wpfapp", "wpfapp2.csproj");
        }

        ProjectPaths = builder.ToImmutableArray();

        if (BuildCache is { } cache)
        {
            if (cache.RunBuild(StorageDirectory, BuildSolution))
            {
                MessageSink.OnDiagnosticMessage($"Using cached build for {nameof(SolutionFixture)}");
            }
        }
        else
        {
            Directory.CreateDirectory(StorageDirectory);
            BuildSolution(StorageDirectory);
        }

        ReadOnlyDirectoryScope = new(StorageDirectory, setReadOnly: true);

        string AddProjectPath(string directoryName, string projectFileName)
        {
            var projectPath = Path.Combine(StorageDirectory, directoryName, projectFileName);
            builder.Add(projectPath);
            return projectPath;
        }
    }

    private void BuildSolution(string storageDirectory)
    {
        var binlogDir = Path.Combine(storageDirectory, "binlogs");
        Directory.CreateDirectory(binlogDir);

        TestUtil.WriteGlobalJson(storageDirectory);
        RunDotnetCommand("dotnet new sln -n Solution", storageDirectory);

        WithProject("console", string (string dir) =>
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

        WithProject("classlib", string (string dir) =>
        {
            RunDotnetCommand($"new classlib --name classlib --framework {TestUtil.TestTargetFramework} -o .", dir);
            return Path.Combine(dir, "classlib.csproj");
        });

        WithProject("classlibmulti", string (string dir) =>
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

        WithProject("classlibwithresources", string (string dir) =>
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
            WithProject("wpfapp", string (string dir) =>
            {
                RunDotnetCommand("new wpf --name wpfapp2 -o .", dir);
                return Path.Combine(dir, "wpfapp2.csproj");
            });
        }

        RunDotnetCommand($"build -bl:{Path.Combine(binlogDir, "msbuild.binlog")} -nr:false", storageDirectory);

        void WithProject(string name, Func<string, string> func)
        {
            var dir = Path.Combine(storageDirectory, name);
            Directory.CreateDirectory(dir);
            var projectPath = func(dir);
            RunDotnetCommand($@"dotnet sln add ""{projectPath}""", storageDirectory);
        };
    }

    public void Dispose()
    {
        ReadOnlyDirectoryScope.ClearReadOnly();
        if (BuildCache is null)
        {
            Directory.Delete(StorageDirectory, recursive: true);
        }
    }
}
