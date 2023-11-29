
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;

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

    internal string BinaryLogPath { get; }

    internal string ConsoleProjectPath { get; }

    internal string ConsoleProjectName => Path.GetFileName(ConsoleProjectPath);

    internal string ClassLibProjectPath { get; }

    public SolutionFixture(IMessageSink messageSink)
        : base(messageSink)
    {
        StorageDirectory = Path.Combine(Path.GetTempPath(), nameof(CompilerLogFixture), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StorageDirectory);
        SolutionPath = Path.Combine(StorageDirectory, "Solution.sln");

        RunDotnetCommand("new globaljson --sdk-version 7.0.400", StorageDirectory);
        RunDotnetCommand("dotnet new sln -n Solution", StorageDirectory);

        var builder = ImmutableArray.CreateBuilder<string>();

        ConsoleProjectPath = WithProject("console", string (string dir) =>
        {
            RunDotnetCommand("new console --name console -o .", dir);
            return Path.Combine(dir, "console.csproj");
        });

        ClassLibProjectPath = WithProject("classlib", string (string dir) =>
        {
            RunDotnetCommand("new classlib --name classlib -o .", dir);
            return Path.Combine(dir, "classlib.csproj");
        });

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
        DotnetUtil.CommandOrThrow("dotnet build -bl -nr:false", StorageDirectory);
        BinaryLogPath = Path.Combine(StorageDirectory, "msbuild.binlog");
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