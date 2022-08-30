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

    /// <summary>
    /// Using a theory to validate file name doesn't impact the hashing
    /// </summary>
    [Theory]
    [InlineData("hello.cs")]
    [InlineData("extra.cs.cs")]
    public void ReadSourceContentHashesSimple(string fileName)
    {
        using var projectBuilder = new ProjectBuilder("example.csproj");
        projectBuilder.AddSourceFile(fileName, "hello world");
        var compilerCall = CreateCSharpCall(projectBuilder);
        using var stream = new MemoryStream();
        var builder = new CompilerLogBuilder(stream, new());
        builder.Add(compilerCall);
        Assert.Empty(builder.Diagnostics);
        builder.Close();
        stream.Position = 0;
        using var reader = new CompilerLogReader(stream, leaveOpen: true);
        Assert.Equal(
            new[] { "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9" },
            reader.ReadSourceContentHashes());
    }

    [Fact]
    public void ReadSourceContentHashesMultple()
    {
        using var projectBuilder = new ProjectBuilder("example.csproj");
        projectBuilder.AddSourceFile("test1.cs", "hello world");
        projectBuilder.AddSourceFile("test2.cs", "// this is a comment");
        var compilerCall = CreateCSharpCall(projectBuilder);
        using var stream = new MemoryStream();
        var builder = new CompilerLogBuilder(stream, new());
        builder.Add(compilerCall);
        Assert.Empty(builder.Diagnostics);
        builder.Close();
        stream.Position = 0;
        using var reader = new CompilerLogReader(stream, leaveOpen: true);
        Assert.Equal(
            new[]
            {
                "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9",
                "E1C6C8930672417F28AFCC7F4D41E3B3D7482C5F1BEDA60202A35F067C6A0866"
            },
            reader.ReadSourceContentHashes());
    }
}