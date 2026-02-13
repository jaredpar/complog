using System.Drawing.Drawing2D;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Core(
                """
                is_global = true
                [c:/example.cs]
                """,
                """
                is_global = true
                [d:/example.cs]
                """,
                x => x.Replace("c:", "d:"));

            Core(
                """
                is_global = true
                [c:\example.cs]
                """,
                """
                is_global = true
                [d:/example.cs]
                """,
                x => x.Replace("c:", "d:"));

            Core(
                """
                is_global = true
                [c:\example.cs]
                """,
                """
                is_global = true
                [c:/test.cs]
                """,
                x => x.Replace("example", "test"));

            Core(
                """
                is_global = true
                [c:/example.cs]
                """,
                """
                is_global = true
                [c:/test.cs]
                """,
                x => x.Replace("example", "test"));
        }
        else
        {
            Core(
                """
                is_global = true
                [/c/example.cs]
                """,
                """
                is_global = true
                [/d/example.cs]
                """,
                x => x.Replace("/c", "/d"));

            Core(
                """
                is_global = true
                [/c/example.cs]
                """,
                """
                is_global = true
                [/c/test.cs]
                """,
                x => x.Replace("example", "test"));
        }

        void Core(string content, string expected, Func<string, string> mapFunc)
        {
            var sourceText = SourceText.From(content);
            var actual = RoslynUtil.RewriteGlobalEditorConfigSections(sourceText, mapFunc);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("build_property.ProjectDir", true)]
    [InlineData("build_property.OutputPath", true)]
    [InlineData("build_property.IntermediateOutputPath", true)]
    [InlineData("build_property.MSBuildProjectDirectory", true)]
    [InlineData("build_property.CsWin32InputMetadataPaths", true)]
    [InlineData("build_property.CryptoKeyFile", true)]
    [InlineData("build_property.SomeFolder", true)]
    [InlineData("build_property.RootNamespace", false)]
    [InlineData("build_property.TargetFramework", false)]
    [InlineData("build_property.AssemblyName", false)]
    [InlineData("dotnet_diagnostic.CA1234.severity", false)]
    [InlineData("indent_style", false)]
    [InlineData("charset", false)]
    [InlineData("is_global", false)]
    [InlineData("", false)]
    [InlineData("a.b", false)]
    public void IsLikelyPathEditorConfigKey(string key, bool expected)
    {
        Assert.Equal(expected, RoslynUtil.IsLikelyPathEditorConfigKey(key.AsSpan()));
    }

    [Fact]
    public void RewriteGlobalEditorConfigPathValues()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Core(
                """
                is_global = true
                [c:\example.cs]
                build_property.ProjectDir = c:\src\project\
                build_property.RootNamespace = MyProject
                """,
                """
                is_global = true
                [d:/example.cs]
                build_property.ProjectDir = d:\src\project\
                build_property.RootNamespace = MyProject
                """,
                x => x.Replace("c:", "d:"));

            Core(
                """
                is_global = true
                [c:\src\project\Project.csproj]
                build_property.ProjectDir = c:\src\project\
                build_property.OutputPath = c:\src\project\bin\Debug\
                build_property.TargetFramework = net8.0
                """,
                """
                is_global = true
                [d:/src/project/Project.csproj]
                build_property.ProjectDir = d:\src\project\
                build_property.OutputPath = d:\src\project\bin\Debug\
                build_property.TargetFramework = net8.0
                """,
                x => x.Replace("c:", "d:"));
        }
        else
        {
            Core(
                """
                is_global = true
                [/src/project/Project.csproj]
                build_property.ProjectDir = /src/project/
                build_property.RootNamespace = MyProject
                """,
                """
                is_global = true
                [/new/project/Project.csproj]
                build_property.ProjectDir = /new/project/
                build_property.RootNamespace = MyProject
                """,
                x => x.Replace("/src/project", "/new/project"));

            Core(
                """
                is_global = true
                [/src/project/Project.csproj]
                build_property.ProjectDir = /src/project/
                build_property.OutputPath = /src/project/bin/Debug/
                build_property.TargetFramework = net8.0
                """,
                """
                is_global = true
                [/new/project/Project.csproj]
                build_property.ProjectDir = /new/project/
                build_property.OutputPath = /new/project/bin/Debug/
                build_property.TargetFramework = net8.0
                """,
                x => x.Replace("/src/project", "/new/project"));
        }

        void Core(string content, string expected, Func<string, string> mapFunc)
        {
            var sourceText = SourceText.From(content);
            var actual = RoslynUtil.RewriteGlobalEditorConfigSections(sourceText, mapFunc);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void RewriteGlobalEditorConfigPathValueNoEquals()
    {
        // A line with a path-like key but no equals sign should be left as-is
        Core(
            """
            is_global = true
            build_property.ProjectDir
            [/example]
            """,
            """
            is_global = true
            build_property.ProjectDir
            [/new]
            """,
            x => x.Replace("/example", "/new"));

        void Core(string content, string expected, Func<string, string> mapFunc)
        {
            var sourceText = SourceText.From(content);
            var actual = RoslynUtil.RewriteGlobalEditorConfigSections(sourceText, mapFunc);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void RewriteGlobalEditorConfigPathValueSpacesAroundEquals()
    {
        // Spaces before and after the equals sign should be preserved
        Core(
            """
            is_global = true
            [/example]
            build_property.ProjectDir   =   /src/project/
            """,
            """
            is_global = true
            [/new]
            build_property.ProjectDir   =   /new/project/
            """,
            x => x.Replace("/src/project", "/new/project").Replace("/example", "/new"));

        void Core(string content, string expected, Func<string, string> mapFunc)
        {
            var sourceText = SourceText.From(content);
            var actual = RoslynUtil.RewriteGlobalEditorConfigSections(sourceText, mapFunc);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void RewriteGlobalEditorConfigPathValueEmpty()
    {
        // An empty value after the equals sign should be left as-is
        Core(
            """
            is_global = true
            [/example]
            build_property.ProjectDir =
            build_property.OutputPath =
            """,
            """
            is_global = true
            [/new]
            build_property.ProjectDir =
            build_property.OutputPath =
            """,
            x => x.Replace("/example", "/new"));

        void Core(string content, string expected, Func<string, string> mapFunc)
        {
            var sourceText = SourceText.From(content);
            var actual = RoslynUtil.RewriteGlobalEditorConfigSections(sourceText, mapFunc);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void RewriteGlobalEditorConfigSkipsComments()
    {
        Core(
            """
            is_global = true
            ; build_property.ProjectDir = /old/path
            # build_property.OutputPath = /old/path
            [/example]
            """,
            """
            is_global = true
            ; build_property.ProjectDir = /old/path
            # build_property.OutputPath = /old/path
            [/new]
            """,
            x => x.Replace("/example", "/new"));

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

    [Fact]
    public void GetAnalyzerTypeDefinitions()
    {
        var image = LibraryUtil.GetAnalyzersWithDiffAttribtueCombinations().Image;
        using var peReader = new PEReader(image);
        var metadataReader = peReader.GetMetadataReader();

        var list = RoslynUtil.GetAnalyzerTypeDefinitions(metadataReader, LanguageNames.CSharp).ToList();
        Assert.Single(list);
        list = RoslynUtil.GetAnalyzerTypeDefinitions(metadataReader, LanguageNames.VisualBasic).ToList();
        Assert.Single(list);
        list = RoslynUtil.GetAnalyzerTypeDefinitions(metadataReader, languageName: null).ToList();
        Assert.Equal(2, list.Count);

        list = RoslynUtil.GetGeneratorTypeDefinitions(metadataReader, LanguageNames.CSharp).ToList();
        Assert.Equal(2, list.Count);
        list = RoslynUtil.GetGeneratorTypeDefinitions(metadataReader, null).ToList();
        Assert.Equal(3, list.Count);
        list = RoslynUtil.GetAnalyzerTypeDefinitions(metadataReader, LanguageNames.VisualBasic).ToList();
        Assert.Single(list);
    }

    [Fact]
    public void ReadMvidSimple()
    {
        var path = typeof(RoslynUtil).Assembly.Location;
        var mvid = RoslynUtil.ReadMvid(path);
        Assert.NotEqual(Guid.Empty, mvid);
    }

    [Fact]
    public void ReadAssemblyNameSimple()
    {
        var path = typeof(RoslynUtil).Assembly.Location;
        var name = RoslynUtil.ReadAssemblyName(path);
        Assert.Equal("Basic.CompilerLog.Util", name);
    }

    [Fact]
    public void TryReadMvid_FileMissing()
    {
        using var temp = new TempDir();
        Assert.Null(RoslynUtil.TryReadMvid(Path.Combine(temp.DirectoryPath, "test.dll")));
    }

    [Fact]
    public void TryReadMvid_FileNotPE()
    {
        using var temp = new TempDir();
        var filePath = temp.NewFile("test.dll", "hello world");
        Assert.Null(RoslynUtil.TryReadMvid(filePath));
    }

    private void WithCompilerCopy(Action<string> action)
    {
        using var temp = new TempDir();
        var sdkDirectory = SdkUtil.GetLatestSdkDirectory().SdkDirectory;
        var roslynDir = Path.Combine(sdkDirectory, "Roslyn", "bincore");
        foreach (var file  in (string[])["csc.dll", "vbc.dll"])
        {
            var srcPath = Path.Combine(roslynDir, file);
            var destPath = Path.Combine(temp.DirectoryPath, file);
            File.Copy(srcPath, destPath);
        }
        action(temp.DirectoryPath);

    }

    [Fact]
    public void GetCompilerInfoNoAppHost()
    {
        WithCompilerCopy(dir =>
        {
            var (name, commit) = RoslynUtil.GetCompilerInfo(Path.Combine(dir, "csc.dll"), true);
            Assert.StartsWith("csc", name.ToString());
            Assert.NotNull(commit);
        });
    }

    [Theory]
    [InlineData(true, "csc")]
    [InlineData(false, "vbc")]
    public void GetCompilerInfoAppHost(bool isCSharp, string expectedName)
    {
        WithCompilerCopy(dir =>
        {
            var appHostName = RoslynUtil.GetCompilerAppFileName(isCSharp);
            var appHostPath = Path.Combine(dir, appHostName);
            File.WriteAllText(appHostPath, "This is a fake app host file");
            var (name, commit) = RoslynUtil.GetCompilerInfo(appHostPath, isCSharp);
            Assert.StartsWith(expectedName, name.ToString());
            Assert.NotNull(commit);
        });
    }

    [Fact]
    public void GetCompilerInfoBadCsc()
    {
        WithCompilerCopy(dir =>
        {
            var filePath = Path.Combine(dir, "csc.dll");
            File.WriteAllText(filePath, "not a pe file");
            var (name, commit) = RoslynUtil.GetCompilerInfo(filePath, true);
            Assert.StartsWith("csc", name.ToString());
            Assert.Null(commit);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryGetCompilerInvocationEmptyDirectory(bool isCSharp)
    {
        using var temp = new TempDir();
        var result = RoslynUtil.TryGetCompilerInvocation(temp.DirectoryPath, isCSharp, out var invocation);
        Assert.False(result);
        Assert.Null(invocation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetCompilerInvocationEmptyDirectory(bool isCSharp)
    {
        using var temp = new TempDir();
        var ex = Assert.Throws<InvalidOperationException>(() => 
            RoslynUtil.GetCompilerInvocation(temp.DirectoryPath, isCSharp));
        Assert.Contains(isCSharp ? "csc" : "vbc", ex.Message);
        Assert.Contains(temp.DirectoryPath, ex.Message);
    }

    [Theory]
    [InlineData(true, "csc.dll")]
    [InlineData(false, "vbc.dll")]
    public void TryGetCompilerInvocationDllOnly(bool isCSharp, string dllName)
    {
        WithCompilerCopy(dir =>
        {
            var result = RoslynUtil.TryGetCompilerInvocation(dir, isCSharp, out var invocation);
            Assert.True(result);
            Assert.NotNull(invocation);
            Assert.Contains("dotnet exec", invocation);
            Assert.Contains(dllName, invocation);
        });
    }

    [Theory]
    [InlineData(true, "csc.dll")]
    [InlineData(false, "vbc.dll")]
    public void GetCompilerInvocationDllOnly(bool isCSharp, string dllName)
    {
        WithCompilerCopy(dir =>
        {
            var invocation = RoslynUtil.GetCompilerInvocation(dir, isCSharp);
            Assert.Contains("dotnet exec", invocation);
            Assert.Contains(dllName, invocation);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryGetCompilerInvocationAppHost(bool isCSharp)
    {
        WithCompilerCopy(dir =>
        {
            var appHostName = RoslynUtil.GetCompilerAppFileName(isCSharp);
            var appHostPath = Path.Combine(dir, appHostName);
            File.WriteAllText(appHostPath, "fake app host");
            
            var result = RoslynUtil.TryGetCompilerInvocation(dir, isCSharp, out var invocation);
            Assert.True(result);
            Assert.NotNull(invocation);
            Assert.DoesNotContain("dotnet exec", invocation);
            Assert.Contains(appHostName, invocation);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetCompilerInvocationAppHost(bool isCSharp)
    {
        WithCompilerCopy(dir =>
        {
            var appHostName = RoslynUtil.GetCompilerAppFileName(isCSharp);
            var appHostPath = Path.Combine(dir, appHostName);
            File.WriteAllText(appHostPath, "fake app host");
            
            var invocation = RoslynUtil.GetCompilerInvocation(dir, isCSharp);
            Assert.DoesNotContain("dotnet exec", invocation);
            Assert.Contains(appHostName, invocation);
        });
    }

    [Theory]
    [InlineData(true, "csc.dll")]
    [InlineData(false, "vbc.dll")]
    public void TryGetCompilerInvocationWithSpacesInPath(bool isCSharp, string dllName)
    {
        using var temp = new TempDir();
        var dirWithSpaces = Path.Combine(temp.DirectoryPath, "path with spaces");
        Directory.CreateDirectory(dirWithSpaces);
        
        var sdkDirectory = SdkUtil.GetLatestSdkDirectory().SdkDirectory;
        var roslynDir = Path.Combine(sdkDirectory, "Roslyn", "bincore");
        var srcPath = Path.Combine(roslynDir, dllName);
        var destPath = Path.Combine(dirWithSpaces, dllName);
        File.Copy(srcPath, destPath);
        
        var result = RoslynUtil.TryGetCompilerInvocation(dirWithSpaces, isCSharp, out var invocation);
        Assert.True(result);
        Assert.NotNull(invocation);
        Assert.Contains("\"", invocation);
        Assert.Contains(dllName, invocation);
    }
}
