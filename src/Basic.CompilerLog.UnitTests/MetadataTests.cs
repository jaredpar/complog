using Basic.CompilerLog.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class MetadataTests
{
    private static Metadata Parse(string content)
    {
        using var reader = new StringReader(content);
        return Metadata.Read(reader);
    }

    [Fact]
    public void ParseVersion0()
    {
        var content = """
            count:50
            """;
        var metadata = Parse(content);
        Assert.Equal(0, metadata.MetadataVersion);
        Assert.Equal(50, metadata.Count);
        Assert.Null(metadata.IsWindows);
    }

    [Fact]
    public void ParseVersion1()
    {
        var content = """
            metadata:1
            count:50
            windows:true
            """;
        var metadata = Parse(content);
        Assert.Equal(1, metadata.MetadataVersion);
        Assert.Equal(50, metadata.Count);
        Assert.True(metadata.IsWindows);
    }
}
