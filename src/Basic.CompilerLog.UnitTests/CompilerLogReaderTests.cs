using Basic.CompilerLog.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

public sealed class CompilerLogReaderTests : TestBase
{
    public CompilerLogReaderTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper, nameof(CompilerLogReader))
    {

    }

    /// <summary>
    /// Ensure that we can process the contents of all the major templates
    /// </summary>
    [Theory]
    [InlineData("console")]
    [InlineData("classlib")]
    public void ReadDifferntTemplates(string template)
    {
        RunDotNet($"new {template} --name example --output .");
        RunDotNet("build -bl");

        using var reader = CompilerLogReader.Create(Path.Combine(RootDirectory, "msbuild.binlog"));
        var compilerCall = reader.ReadCompilerCall(0);
        Assert.True(compilerCall.IsCSharp);

        var compilationData = reader.ReadCompilationData(compilerCall);
        Assert.NotNull(compilationData);
    }

    /// <summary>
    /// Can we process an extra file in the major templates
    /// </summary>
    /// <param name="template"></param>
    [Theory]
    [InlineData("console")]
    [InlineData("classlib")]
    public void ContentExtraSourceFile(string template)
    {
        RunDotNet($"new {template} --name example --output .");
        var content = """
            // Example content
            """;
        File.WriteAllText(Path.Combine(RootDirectory, "extra.cs"), content, DefaultEncoding);
        RunDotNet("build -bl");

        using var reader = CompilerLogReader.Create(Path.Combine(RootDirectory, "msbuild.binlog"));
        var rawData = reader.ReadRawCompilationData(0).Item2;
        var extraData = rawData.Contents.Single(x => Path.GetFileName(x.FilePath) == "extra.cs");
        var contentBytes = reader.GetContentBytes(extraData.ContentHash);
        Assert.Equal(content, DefaultEncoding.GetString(contentBytes));
    }
}
