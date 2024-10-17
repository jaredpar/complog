using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Basic.CompilerLog.UnitTests;

public abstract class TestBase : IDisposable
{
    private static readonly object Guard = new();

    internal static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private List<string> BadAssemblyLoadList { get; } = new();
    internal ITestOutputHelper TestOutputHelper { get; }
    internal TempDir Root { get; }
    internal Util.LogReaderState State { get; }
    internal string RootDirectory => Root.DirectoryPath;

    // Have simple helpers in a real tfm (i.e. not netstandard)
#if NET 
    internal static bool IsNetCore => true;
    internal static bool IsNetFramework => false;
#else
    internal static bool IsNetCore => false;
    internal static bool IsNetFramework => true;
#endif

    /// <summary>
    /// Get all of the supported <see cref="BasicAnalyzerKind"/>
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<object[]> GetSupportedBasicAnalyzerKinds()
    {
        yield return new object[] { BasicAnalyzerKind.None };
        yield return new object[] { BasicAnalyzerKind.OnDisk };

        if (IsNetCore)
        {
            yield return new object[] { BasicAnalyzerKind.InMemory };
        }
    }

    /// <summary>
    /// Return the <see cref="BasicAnalyzerKind"/> that do not pollute address space and 
    /// can be run simply.
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<object[]> GetSimpleBasicAnalyzerKinds()
    {
        yield return new object[] { BasicAnalyzerKind.None };

        if (IsNetCore)
        {
            yield return new object[] { BasicAnalyzerKind.OnDisk };
            yield return new object[] { BasicAnalyzerKind.InMemory };
        }
    }

    protected TestBase(ITestOutputHelper testOutputHelper, string name)
    {
        TestOutputHelper = testOutputHelper;
        Root = new TempDir(name);
        State = new Util.LogReaderState(Root.NewDirectory("state"));
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    public virtual void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        if (BadAssemblyLoadList.Count > 0)
        {
            TestOutputHelper.WriteLine("Bad assembly loads");
            foreach (var assemblyFilePath in BadAssemblyLoadList)
            {
                TestOutputHelper.WriteLine($"\t{assemblyFilePath}");
            }
            Assert.Fail("Bad assembly loads");
        }
        TestOutputHelper.WriteLine("Deleting temp directory");
        Root.Dispose();
    }

    public CompilationData GetCompilationData(
        string complogFilePath,
        Func<CompilerCall, bool>? predicate = null,
        BasicAnalyzerKind? basicAnalyzerKind = null)
    {
        using var reader = CompilerLogReader.Create(complogFilePath, basicAnalyzerKind, State);
        return reader.ReadAllCompilationData(predicate).Single();
    }

    protected void RunDotNet(string command, string? workingDirectory = null)
    {
        workingDirectory ??= RootDirectory;
        TestOutputHelper.WriteLine($"Working directory: {workingDirectory}");
        TestOutputHelper.WriteLine($"Executing: dotnet {command}");

        ProcessResult result;

        // There is a bug in the 7.0 SDK that causes an exception if multiple dotnet new commands
        // are run in parallel. This can happen with our tests. Temporarily guard against this 
        // with a lock
        // https://github.com/dotnet/sdk/pull/28677
        lock (Guard)
        {
            result = DotnetUtil.Command(command, workingDirectory);
        }

        TestOutputHelper.WriteLine(result.StandardOut);
        TestOutputHelper.WriteLine(result.StandardError);
        Assert.Equal(0, result.ExitCode);
    }

    protected void AddProjectProperty(string property, string? workingDirectory = null)
    {
        workingDirectory ??= RootDirectory;
        var projectFile = Directory.EnumerateFiles(workingDirectory, "*proj").Single();
        var lines = File.ReadAllLines(projectFile);
        using var writer = new StreamWriter(projectFile, append: false);
        foreach (var line in lines)
        {
            if (line.Contains("</PropertyGroup>"))
            {
                writer.WriteLine(property);
            }

            writer.WriteLine(line);
        }
    }

    protected string GetBinaryLogFullPath(string? workingDirectory = null) =>
        Path.Combine(workingDirectory ?? RootDirectory, "msbuild.binlog");

    protected CompilerLogReader GetReader(bool emptyDirectory = true )
    {
        var reader = CompilerLogReader.Create(GetBinaryLogFullPath());
        if (emptyDirectory)
        {
            Root.EmptyDirectory();
        }

        return reader;
    }

    private protected CompilerLogReader CreateReader(Action<CompilerLogBuilder> action, LogReaderState? state = null)
    {
        var stream = new MemoryStream();
        var diagnostics = new List<string>();
        var builder = new CompilerLogBuilder(stream, diagnostics);
        action(builder);
        builder.Close();
        stream.Position = 0;
        return CompilerLogReader.Create(stream, state, leaveOpen: false);
    }

    /// <summary>
    /// Run the build.cmd / .sh generated from an export command
    /// </summary>
    internal static ProcessResult RunBuildCmd(string directory) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
         ? ProcessUtil.Run("cmd", args: "/c build.cmd", workingDirectory: directory)
         : ProcessUtil.Run(Path.Combine(directory, "build.sh"), args: "", workingDirectory: directory);

    protected void RunInContext<T>(T state, Action<ITestOutputHelper, T> action, [CallerMemberName] string? testMethod = null)
    {
#if NETFRAMEWORK
        AppDomain? appDomain = null;
        try
        {
            appDomain = AppDomainUtils.Create($"Test {testMethod}");
            var testOutputHelper = new AppDomainTestOutputHelper(TestOutputHelper);
            var type = typeof(InvokeUtil);
            var util = (InvokeUtil)appDomain.CreateInstanceAndUnwrap(type.Assembly.FullName, type.FullName);
            util.Invoke(action.Method.DeclaringType.FullName, action.Method.Name, testOutputHelper, state);
        }
        finally
        {
            AppDomain.Unload(appDomain);
        }
#else
        // On .NET Core the analyzers naturally load into child load contexts so no need for complicated
        // marshalling here.
        action(TestOutputHelper, state);
#endif

    }

    /// <summary>
    /// This tracks assembly loads to make sure that we aren't polluting the main <see cref="AppDomain"/>
    /// When we load analyzers from tests into the main <see cref="AppDomain"/> that potentially pollutes
    /// or interferes with other tests. Instead each test should be loading into their own.
    /// </summary>
    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs e)
    {
        var testBaseAssembly = typeof(TestBase).Assembly;
        var assembly = e.LoadedAssembly;
        if (assembly.IsDynamic)
        {
            return;
        }

#if NETFRAMEWORK
        if (assembly.GlobalAssemblyCache)
        {
            return;
        }

        string[] legalDirs =
        [
            Path.GetDirectoryName(testBaseAssembly.Location)!,
        ];
#else
        var mainContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(testBaseAssembly);
        var assemblyContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(e.LoadedAssembly);
        if (mainContext != assemblyContext)
        {
            return;
        }

        string[] legalDirs =
        [
            Path.GetDirectoryName(testBaseAssembly.Location)!,
            Path.GetDirectoryName(typeof(object).Assembly.Location)!
        ];
#endif

        var testDir = Path.GetDirectoryName(testBaseAssembly.Location);
        var assemblyDir = Path.GetDirectoryName(e.LoadedAssembly.Location);
        var any = false;
        foreach (var legalDir in legalDirs)
        {
            if (string.Equals(legalDir, assemblyDir, PathUtil.Comparison))
            {
                any = true;
            }
        }

        if (!any)
        {
            BadAssemblyLoadList.Add(Path.GetFileName(e.LoadedAssembly.Location));
        }
    }
}
