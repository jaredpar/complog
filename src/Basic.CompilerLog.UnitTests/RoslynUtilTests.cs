using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class RoslynUtilTests
{
    [Fact]
    public void ParseAllVisualBasicEmpty()
    {
        var result = RoslynUtil.ParseAllVisualBasic([], VisualBasicParseOptions.Default);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseAllCSharpEmpty()
    {
        var result = RoslynUtil.ParseAllCSharp([], CSharpParseOptions.Default);
        Assert.Empty(result);
    }
}