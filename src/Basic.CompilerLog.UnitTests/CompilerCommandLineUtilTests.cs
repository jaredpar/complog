using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.Options;
using System.Text;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class CompilerCommandLineUtilTests
{
    [Theory]
    [InlineData("/reference:test.dll", true)]
    [InlineData("/r:test.dll", true)]
    [InlineData("/out:output.exe", true)]
    [InlineData("/debug+", true)]
    [InlineData("/debug-", true)]
    [InlineData("/optimize", true)]
    [InlineData("-reference:test.dll", true)]
    [InlineData("-debug+", true)]
    [InlineData("/nullable+", true)]
    [InlineData("/nullable-", true)]
    [InlineData("/warnaserror+:CA2000", true)]
    [InlineData("/warnaserror-:CA2000", true)]
    [InlineData("/REFERENCE:test.dll", true)]
    [InlineData("/Reference:test.dll", true)]
    [InlineData("/target:library", true)]
    [InlineData("/t:exe", true)]
    [InlineData("source.cs", false)]
    [InlineData("path/to/file.cs", false)]
    [InlineData("", false)]
    [InlineData("reference:test.dll", false)]
    [InlineData("//reference:test.dll", false)]
    public void IsOption(string arg, bool expected)
    {
        Assert.Equal(expected, CompilerCommandLineUtil.IsOption(arg));
    }

    [Theory]
    [InlineData("reference", true)]
    [InlineData("r", true)]
    [InlineData("analyzer", true)]
    [InlineData("a", true)]
    [InlineData("additionalfile", true)]
    [InlineData("analyzerconfig", true)]
    [InlineData("resource", true)]
    [InlineData("res", true)]
    [InlineData("linkresource", true)]
    [InlineData("linkres", true)]
    [InlineData("sourcelink", true)]
    [InlineData("ruleset", true)]
    [InlineData("keyfile", true)]
    [InlineData("link", true)]
    [InlineData("l", true)]
    [InlineData("out", true)]
    [InlineData("refout", true)]
    [InlineData("doc", true)]
    [InlineData("generatedfilesout", true)]
    [InlineData("pdb", true)]
    [InlineData("errorlog", true)]
    [InlineData("win32manifest", true)]
    [InlineData("win32res", true)]
    [InlineData("win32icon", true)]
    [InlineData("addmodule", true)]
    [InlineData("appconfig", true)]
    [InlineData("lib", true)]
    [InlineData("debug", false)]
    [InlineData("optimize", false)]
    [InlineData("target", false)]
    [InlineData("nullable", false)]
    [InlineData("unsafe", false)]
    [InlineData("platform", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void IsPathOption(string optionName, bool expected)
    {
        var option = new CompilerCommandLineUtil.OptionParts('/', optionName, CompilerCommandLineUtil.OptionSuffix.Colon, "");
        Assert.Equal(expected, CompilerCommandLineUtil.IsPathOption(option));
    }

    [Theory]
    [InlineData("/embed", false)]
    [InlineData("-embed", false)]
    [InlineData("-embed:hello", true)]
    public void IsPathOption_Embed(string option, bool expected)
    {
        Assert.Equal(expected, CompilerCommandLineUtil.IsPathOption(option));
    }

    [Theory]
    [InlineData("/reference:test.dll", true, '/', true, "reference", "test.dll")]
    [InlineData("/r:test.dll", true, '/', true, "r", "test.dll")]
    [InlineData("/out:output.exe", true, '/', true, "out", "output.exe")]
    [InlineData("/debug+", true, '/', false, "debug", "")]
    [InlineData("/debug-", true, '/', false, "debug", "")]
    [InlineData("/optimize", true, '/', false, "optimize", "")]
    [InlineData("-reference:test.dll", true, '-', true, "reference", "test.dll")]
    [InlineData("-debug+", true, '-', false, "debug", "")]
    [InlineData("/REFERENCE:Test.dll", true, '/', true, "reference", "Test.dll")]
    [InlineData("/Reference:Test.dll", true, '/', true, "reference", "Test.dll")]
    [InlineData("/nullable:enable", true, '/', true, "nullable", "enable")]
    [InlineData("/target:library", true, '/', true, "target", "library")]
    [InlineData("source.cs", false, default(char), false, "", "")]
    public void TryParseOption(
        string arg,
        bool expectedResult,
        char expectedPrefix,
        bool expectedHasColon,
        string expectedName,
        string expectedValue)
    {
        var result = CompilerCommandLineUtil.TryParseOption(arg, out var optionParts);
        Assert.Equal(expectedResult, result);

        if (result)
        {
            Assert.Equal(expectedPrefix, optionParts.Prefix);
            Assert.Equal(expectedHasColon, optionParts.HasColon);
            Assert.Equal(expectedName, optionParts.Name.ToString());
            Assert.Equal(expectedValue, optionParts.Value.ToString());
        }
    }

    [Fact]
    public void TryParseOption_LowercasesOptionName()
    {
        Assert.True(CompilerCommandLineUtil.TryParseOption("/REFERENCE:Test.dll", out var option));
        Assert.Equal("reference", option.Name.ToString());

        Assert.True(CompilerCommandLineUtil.TryParseOption("/Reference:Test.dll", out option));
        Assert.Equal("reference", option.Name.ToString());

        Assert.True(CompilerCommandLineUtil.TryParseOption("/TARGET:library", out option));
        Assert.Equal("target", option.Name.ToString());
    }

    [Theory]
    [InlineData(@"/reference:a,b")]
    [InlineData(@"/reference:""a"",b")]
    public void TryParseOption_References(string str)
    {
        Assert.True(CompilerCommandLineUtil.TryParseOption(str, out var option));
        Assert.True(CompilerCommandLineUtil.IsPathOption(option));
        Assert.Equal("reference", option.Name);
    }

    [Theory]
    [InlineData("/errorlog:build.sarif", "build.sarif", "")]
    [InlineData("/errorlog:build.sarif,version=2", "build.sarif", "version=2")]
    [InlineData("/errorlog:build.sarif,version=2.1", "build.sarif", "version=2.1")]
    [InlineData("/errorlog:path/to/build.sarif", "path/to/build.sarif", "")]
    [InlineData("/errorlog:path/to/build.sarif,version=1", "path/to/build.sarif", "version=1")]
    public void ParseErrorLogArgument(string arg, string expectedPath, string expectedVersion)
    {
        Assert.True(CompilerCommandLineUtil.TryParseOption(arg, out var option));
        CompilerCommandLineUtil.ParseErrorLogArgument(option, out var path, out var version);
        Assert.Equal(expectedPath, path.ToString());
        Assert.Equal(expectedVersion, version.ToString());
    }

    [Theory]
    [InlineData("/errorlog:\"path with spaces/build.sarif\"", "path with spaces/build.sarif", "")]
    [InlineData("/errorlog:\"path with spaces/build.sarif,version=2\"", "path with spaces/build.sarif", "version=2")]
    public void ParseErrorLogArgument_QuotedPath(string arg, string expectedPath, string expectedVersion)
    {
        Assert.True(CompilerCommandLineUtil.TryParseOption(arg, out var option));
        CompilerCommandLineUtil.ParseErrorLogArgument(option, out var path, out var version);
        Assert.Equal(expectedPath, path.ToString());
        Assert.Equal(expectedVersion, version.ToString());
    }

    [Theory]
    [InlineData("/errorlog:build.sarif", "/errorlog:build.sarif")]
    [InlineData("/errorlog:build.sarif,version=2", @"/errorlog:""build.sarif,version=2""")]
    public void NormalizeArgument_ErrorLog_NoSpaces(string arg, string expected)
    {
        var util = PathNormalizationUtil.Empty;
        Assert.Equal(expected, CompilerCommandLineUtil.NormalizeArgument(arg, util.NormalizePath));
    }

    [Theory]
    [InlineData(@"/errorlog:c:\src\build.sarif", "/errorlog:/code/src/build.sarif")]
    [InlineData(@"/errorlog:c:\src\build.sarif,version=2", @"/errorlog:""/code/src/build.sarif,version=2""")]
    public void NormalizeArgument_ErrorLog_NormalizesPath(string arg, string expected)
    {
        var util = PathNormalizationUtil.WindowsToUnix;
        Assert.Equal(expected, CompilerCommandLineUtil.NormalizeArgument(arg, util.NormalizePath));
    }

    [Theory]
    [InlineData(@"/errorlog:""c:\path with spaces\build.sarif""", @"/errorlog:""/code/path with spaces/build.sarif""")]
    [InlineData(@"/errorlog:""c:\path with spaces\build.sarif,version=2""", @"/errorlog:""/code/path with spaces/build.sarif,version=2""")]
    public void NormalizeArgument_ErrorLog_QuotedPathWithSpaces(string arg, string expected)
    {
        var util = PathNormalizationUtil.WindowsToUnix;
        Assert.Equal(expected, CompilerCommandLineUtil.NormalizeArgument(arg, util.NormalizePath));
    }

    [Theory]
    [InlineData("simple.cs", "simple.cs")]
    [InlineData("path/to/file.cs", "path/to/file.cs")]
    [InlineData("path with spaces/file.cs", "\"path with spaces/file.cs\"")]
    [InlineData("my file.cs", "\"my file.cs\"")]
    [InlineData("nospaces", "nospaces")]
    public void MaybeQuotePath(string path, string expected)
    {
        Assert.Equal(expected, CompilerCommandLineUtil.MaybeQuotePath(path));
    }

    [Theory]
    [InlineData("\"quoted\"", true)]
    [InlineData("\"path with spaces\"", true)]
    [InlineData("\"\"", true)]
    [InlineData("unquoted", false)]
    [InlineData("\"only start", false)]
    [InlineData("only end\"", false)]
    [InlineData("\"", false)]
    [InlineData("", false)]
    public void IsQuoted(string value, bool expected)
    {
        Assert.Equal(expected, CompilerCommandLineUtil.IsQuoted(value));
    }

    [Theory]
    [InlineData("\"quoted\"", "quoted")]
    [InlineData("\"path with spaces\"", "path with spaces")]
    [InlineData("unquoted", "unquoted")]
    [InlineData("noquotes", "noquotes")]
    public void MaybeRemoveQuotes_String(string value, string expected)
    {
        Assert.Equal(expected, CompilerCommandLineUtil.MaybeRemoveQuotes(value));
    }

    [Theory]
    [InlineData("\"quoted\"", "quoted")]
    [InlineData("\"path with spaces\"", "path with spaces")]
    [InlineData("unquoted", "unquoted")]
    public void MaybeRemoveQuotes_Span(string value, string expected)
    {
        ReadOnlySpan<char> span = value;
        var result = CompilerCommandLineUtil.MaybeRemoveQuotes(span);
        Assert.Equal(expected, result.ToString());
    }

    [Theory]
    [InlineData("/debug+", "/debug+")]
    [InlineData("/optimize", "/optimize")]
    [InlineData("/target:library", "/target:library")]
    [InlineData("/nullable:enable", "/nullable:enable")]
    public void NormalizeArgument_NonPathOptions_ReturnsUnchanged(string arg, string expected)
    {
        var util = PathNormalizationUtil.Empty;
        Assert.Equal(expected, CompilerCommandLineUtil.NormalizeArgument(arg, util.NormalizePath));
    }

    [Fact]
    public void NormalizeArgument_SourceFile_NormalizesPath()
    {
        var util = PathNormalizationUtil.WindowsToUnix;
        var result = CompilerCommandLineUtil.NormalizeArgument(@"c:\src\file.cs", util.NormalizePath);
        Assert.Equal("/code/src/file.cs", result);
    }

    [Fact]
    public void NormalizeArgument_QuotedSourceFile_NormalizesPath()
    {
        var util = PathNormalizationUtil.WindowsToUnix;
        var result = CompilerCommandLineUtil.NormalizeArgument(@"""c:\src\my file.cs""", util.NormalizePath);
        Assert.Equal("\"/code/src/my file.cs\"", result);
    }

    [Theory]
    [InlineData(@"/reference:a,b", "a-b-")]
    [InlineData(@"/reference:a,""b c""", "a-b c-")]
    [InlineData(@"/reference:a,""b c"",d", "a-b c-d-")]
    public void NormalizeArgument_PathList(string optionText, string expected)
    {
        var builder = new StringBuilder(expected.Length);
        Assert.True(CompilerCommandLineUtil.TryParseOption(optionText, out var option));
        Assert.Equal(optionText, CompilerCommandLineUtil.NormalizePathOption(option, (x, _) =>
        {
            builder.Append(x);
            builder.Append('-');
            return x;
        }));
        Assert.Equal(expected, builder.ToString());
    }
}
