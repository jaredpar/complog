using Basic.CompilerLog.Util;
using Basic.Reference.Assemblies;
using Microsoft.Build.Construction;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class CompilerLogReaderTests : IDisposable
{
    internal TestableFileSystem FileSystem { get; } = new();
    internal string RootDirectory { get; } = @"c:\sources";
    internal string ReferencesDirectory => Path.Combine(RootDirectory, "references");

    public CompilerLogReaderTests()
    {
        foreach (var referenceInfo in Net60.References.All)
        {
            FileSystem.AddReference(Path.Combine(ReferencesDirectory, referenceInfo.FileName), referenceInfo.ImageBytes);
        }
    }

    // PUT IT ALL ON DISK
    public void Dispose()
    {
        Directory.Delete()

    }

    private void AddSourceFile(string fileRelativePath, string content) =>
        FileSystem.AddSourceFile(
            Path.Combine(RootDirectory, fileRelativePath),
            content);

    private CompilerCall CreateCSharpCall(
        string? projectFile = null,
        string[]? sourceFiles = null)
    {
        var args = new List<string>();

        foreach (var referenceInfo in Net60.References.All)
        {
            args.Add($"/r:{Path.Combine(ReferencesDirectory, referenceInfo.FileName)}");
        }

        if (sourceFiles is not null)
        {
            args.AddRange(sourceFiles);
        }

        return new CompilerCall(
            projectFile ?? Path.Combine(RootDirectory, "example.csproj"),
            CompilerCallKind.Regular,
            "net6.0",
            isCSharp: true,
            args.ToArray());
    }

    [Fact]
    public void ReadSourceContentHashesSimple()
    {
        AddSourceFile("test.cs", "hello world");
        var compilerCall = CreateCSharpCall(sourceFiles: new[] { "test.cs" });
        using var stream = new MemoryStream();
        var builder = new CompilerLogBuilder(stream, new()) { FileSystem = FileSystem };
        builder.Add(compilerCall);
        Assert.Empty(builder.Diagnostics);
        builder.Close();
        stream.Position = 0;
        var reader = CompilerLogReader.Create(stream, leaveOpen: true);
        var hashes = reader.ReadSourceContentHashes();
    }
}