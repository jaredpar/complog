using Basic.CompilerLog.Util;
using Basic.Reference.Assemblies;
using Microsoft.Build.Construction;
using System.ComponentModel;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class CompilerLogReaderTests
{
    public CompilerLogReaderTests()
    {

    }

    private CompilerCall CreateCSharpCall(ProjectBuilder builder)
    {
        var args = new List<string>();

        foreach (var refPath in builder.ReferenceFilePaths)
        {
            args.Add($"/r:{refPath}");
        }

        args.AddRange(builder.SourceFilePaths);

        return new CompilerCall(
            builder.ProjectFilePath,
            CompilerCallKind.Regular,
            "net6.0",
            isCSharp: true,
            args.ToArray());
    }

    [Fact]
    public void ReadSourceContentHashesSimple()
    {
        using var projectBuilder = new ProjectBuilder("example.csproj");
        projectBuilder.AddSourceFile("test.cs", "hello world");
        var compilerCall = CreateCSharpCall(projectBuilder);
        using var stream = new MemoryStream();
        var builder = new CompilerLogBuilder(stream, new());
        builder.Add(compilerCall);
        Assert.Empty(builder.Diagnostics);
        builder.Close();
        stream.Position = 0;
        using var reader = CompilerLogReader.Create(stream, leaveOpen: true);
        Assert.Equal(
            new[] { "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9" },
            reader.ReadSourceContentHashes().ToArray());
    }
}