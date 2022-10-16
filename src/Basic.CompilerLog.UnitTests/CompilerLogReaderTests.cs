using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class CompilerLogReaderTests
{
    public CompilerLogReaderTests()
    {

    }

    private CompilerLogReader GetHelloWorldReader()
    {
        using var projectBuilder = new ProjectBuilder("example.csproj");
        projectBuilder.AddSourceFile("hello-world.cs", @"Console.WriteLine(""Hello World"");");
        return projectBuilder.GetCompilerLogReader();
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
        using var reader = projectBuilder.GetCompilerLogReader();
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
        using var reader = projectBuilder.GetCompilerLogReader();
        Assert.Equal(
            new[]
            {
                "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9",
                "E1C6C8930672417F28AFCC7F4D41E3B3D7482C5F1BEDA60202A35F067C6A0866"
            },
            reader.ReadSourceContentHashes());
    }

    [Fact]
    public void RoundTripReferences()
    {
        using var reader = GetHelloWorldReader();
        var (_, compilationData) = reader.ReadRawCompilationData(0);
        var refSet = new HashSet<Guid>(compilationData.References.Select(x => x.Mvid));
        foreach (var reference in ProjectBuilder.DefaultReferences)
        {
            var mvid = reference.GetModuleVersionId();
            Assert.Contains(mvid, refSet);
            var newRef = reader.GetMetadataReference(mvid);
            Assert.Equal(mvid, newRef.GetModuleVersionId());
        }
    }
}