using System.Drawing.Drawing2D;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class RoslynUtilTests
{
    public readonly struct IsGlobalConfigData(bool expected, string contents, int id)
    {
        public bool IsExpect { get; } = expected;
        public string Contents { get; } = contents;
        public override string ToString() => $"{nameof(IsGlobalConfigData)}-{id}";
    }

    public static IEnumerable<object[]> GetIsGlobalConfigData()
    {
        return GetRaw().Select(x => new object[] { x });

        static IEnumerable<IsGlobalConfigData> GetRaw()
        {
            var index = 0;
            yield return new IsGlobalConfigData(
                false,
                "",
                index++);

            yield return new IsGlobalConfigData(
                false,
                """
                is_global = true
                """,
                index++);

            yield return new IsGlobalConfigData(
                true,
                """
                is_global = true
                [examlpe]
                """,
                index++);

            yield return new IsGlobalConfigData(
                false,
                """
                is_global = false
                """,
                index++);

            yield return new IsGlobalConfigData(
                true,
                """
                is_global = false
                is_global = true
                [example]
                """,
                index++);

            // Don't read past the first section
            yield return new IsGlobalConfigData(
                false,
                """
                [c:\example.cs]
                is_global = false
                is_global = true
                """,
                index++);

            // ignore comments
            yield return new IsGlobalConfigData(
                false,
                """
                ;is_global = true
                a = 3
                [section]
                """,
                index++);

            // Super long lines
            yield return new IsGlobalConfigData(
                false,
                $"""
                ;{new string('a', 1000)}
                ;is_global = true
                a = 3
                """,
                index++);

            // Super long lines
            yield return new IsGlobalConfigData(
                true,
                $"""
                ;{new string('a', 1000)}
                is_global = true
                a = 3
                [section]
                """,
                index++);

            // Every new line combination
            string[] newLines = 
            [
                "\r\n",
                "\n",
                "\r",
                "\u2028",
                "\u2029",
                $"{(char)0x85}"
            ];

            foreach (var newLine in newLines)
            {
                var content = $"is_global = true{newLine}[section]";
                yield return new IsGlobalConfigData(
                    true,
                    content,
                    index++);
            }
        }
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
    public void IsGlobalConfig(IsGlobalConfigData data)
    {
        var sourceText = SourceText.From(data.Contents);
        var actual = RoslynUtil.IsGlobalEditorConfigWithSection(sourceText);
        Assert.Equal(data.IsExpect, actual);
    }

    [Fact]
    public void RewriteGlobalEditorConfigPaths()
    {
        Core(
            """
            is_global = true
            [c:\example.cs]
            """,
            """
            is_global = true
            [d:\example.cs]
            """,
            x => x.Replace("c:", "d:"));

        Core(
            """
            is_global = true
            [c:\example.cs]
            """,
            """
            is_global = true
            [c:\test.cs]
            """,
            x => x.Replace("example", "test"));

        void Core(string content, string expected, Func<string, string> mapFunc)
        {
            var sourceText = SourceText.From(content);
            var actual = RoslynUtil.RewriteGlobalEditorConfigSections(sourceText, mapFunc);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(@"/target:exe", "app.exe")]
    [InlineData(@"/target:winexe", "app.exe")]
    [InlineData(@"/target:module", "app.netmodule")]
    [InlineData(@"/target:library", "app.dll")]
    [InlineData(@"/target:module other.cs", "other.netmodule")]
    [InlineData(@"/target:library other.cs", "other.dll")]
    public void GetAssemblyFileName(string commandLine, string expectedFileName)
    {
        var args = CSharpCommandLineParser.Default.Parse(
            commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            baseDirectory: Path.Combine(ResilientDirectoryTests.RootPath, "code"),
            sdkDirectory: null,
            additionalReferenceDirectories: null);
        var actualFileName = RoslynUtil.GetAssemblyFileName(args);
        Assert.Equal(expectedFileName, actualFileName);
    }

}