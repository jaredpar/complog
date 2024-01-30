using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class RoslynUtilTests
{
    public static IEnumerable<object[]> GetIsGlobalConfigData()
    {
        yield return new object[]
        { 
            true,
            """
            is_global = true
            """
        };

        yield return new object[]
        { 
            false,
            """
            is_global = false
            """
        };

        yield return new object[]
        { 
            true,
            """
            is_global = false
            is_global = true
            """
        };

        // Don't read past the first section
        yield return new object[]
        { 
            false,
            """
            [c:\example.cs]
            is_global = false
            is_global = true
            """
        };

        // ignore comments
        yield return new object[]
        { 
            false,
            """
            ;is_global = true
            a = 3
            """
        };

        // Super long lines
        yield return new object[]
        { 
            false,
            $"""
            ;{new string('a', 1000)}
            ;is_global = true
            a = 3
            """
        };

        // Super long lines
        yield return new object[]
        { 
            true,
            $"""
            ;{new string('a', 1000)}
            is_global = true
            a = 3
            """
        };
    }

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

    [Theory]
    [MemberData(nameof(GetIsGlobalConfigData))]
    public void IsGlobalConfig(bool expected, string contents)
    {
        var sourceText = SourceText.From(contents);
        var actual = RoslynUtil.IsGlobalEditorConfig(sourceText);
        Assert.Equal(expected, actual);
    }
}