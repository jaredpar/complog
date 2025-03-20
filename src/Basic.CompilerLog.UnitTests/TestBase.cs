using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public abstract class TestBase : IDisposable
{
    private static readonly object Guard = new();

    internal static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private List<string> BadAssemblyLoadList { get; } = new();
    public ITestOutputHelper TestOutputHelper { get; }
    public ITestContextAccessor TestContextAccessor { get; }
    
    internal TempDir Root { get; }
    internal LogReaderState State { get; }

    public ITestContext TestContext => TestContextAccessor.Current;
    public CancellationToken CancellationToken => TestContext.CancellationToken;
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
    /// Get all of the <see cref="BasicAnalyzerKind"/>
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<object[]> GetBasicAnalyzerKinds()
    {
        foreach (BasicAnalyzerKind e in Enum.GetValues(typeof(BasicAnalyzerKind)))
        {
            yield return [e];
        }
    }

    /// <summary>
    /// Get all of the supported <see cref="BasicAnalyzerKind"/>
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<object[]> GetSupportedBasicAnalyzerKinds()
    {
        yield return [BasicAnalyzerKind.None];
        yield return [BasicAnalyzerKind.OnDisk];

        if (IsNetCore)
        {
            yield return [BasicAnalyzerKind.InMemory];
        }
    }

    /// <summary>
    /// Return the <see cref="BasicAnalyzerKind"/> that do not pollute address space and 
    /// can be run simply.
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<object[]> GetSimpleBasicAnalyzerKinds()
    {
        yield return [BasicAnalyzerKind.None];

        if (IsNetCore)
        {
            yield return [BasicAnalyzerKind.OnDisk];
            yield return [BasicAnalyzerKind.InMemory];
        }
    }

    /// <summary>
    /// This captures the set of "missing" files that we need to be tolerant of in our 
    /// reading and creation of compiler logs.
    /// </summary>
    public static IEnumerable<object?[]> GetMissingFileArguments()
    {
        yield return ["keyfile", "does-not-exist.snk", false]; // key file isn't noticed until emit
        yield return ["embed", "data.txt", true];
        yield return ["win32manifest", "data.manifest", false]; // manifest isn't noticed until emit
        yield return ["win32res", "data.res", true]; 
        yield return ["sourcelink", "data.link", true];
        yield return ["analyzerconfig", "data.config", true];
        yield return [null, "data.cs", true];
    }

    protected TestBase(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, string name)
    {
        TestOutputHelper = testOutputHelper;
        TestContextAccessor = testContextAccessor;
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

        TestUtil.ClearLocalizableStringMap();

#if NET

        if (OnDiskLoader.AnyActiveAssemblyLoadContext)
        {
            var maxCount = 10;
            for (int i = 0; i < maxCount && OnDiskLoader.AnyActiveAssemblyLoadContext; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

#endif

        State.Dispose();

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

    protected void AddProjectProperty(string property, string? workingDirectory = null) =>
        TestUtil.AddProjectProperty(property, workingDirectory ?? RootDirectory);

    protected void SetProjectFileContent(string content, string? workingDirectory = null) =>
        TestUtil.SetProjectFileContent(content, workingDirectory ?? RootDirectory);

    protected string GetBinaryLogFullPath(string? workingDirectory = null) =>
        Path.Combine(workingDirectory ?? RootDirectory, "msbuild.binlog");

    /// <summary>
    /// Dig through a compiler log for a single <see cref="CompilerCall"/>, change it and get a reader
    /// over a new compiler log built from it.
    /// </summary>
    protected CompilerLogReader ChangeCompilerCall(
        string logFilePath,
        Func<CompilerCall, bool> predicate,
        Func<CompilerCall, CompilerCall> func,
        BasicAnalyzerKind? basicAnalyzerKind = null,
        List<string>? diagnostics = null)
    {
        using var reader = CompilerCallReaderUtil.Create(logFilePath, basicAnalyzerKind, State);
        var compilerCall = reader
            .ReadAllCompilerCalls(predicate)
            .Single();

        compilerCall = func(compilerCall);

        diagnostics ??= new List<string>();
        var stream = new MemoryStream();
        var builder = new CompilerLogBuilder(stream, diagnostics);
        builder.AddFromDisk(compilerCall, BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall));
        builder.Close();
        stream.Position = 0;
        return CompilerLogReader.Create(stream, basicAnalyzerKind, State, leaveOpen: false);
    }

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

    protected void RunInContext<T>(T state, Action<ITestOutputHelper, T, CancellationToken> action, [CallerMemberName] string? testMethod = null)
    {
#if NETFRAMEWORK
        AppDomain? appDomain = null;
        try
        {
            appDomain = AppDomainUtils.Create($"Test {testMethod}");
            var testOutputHelper = new AppDomainTestOutputHelper(TestOutputHelper);
            var type = typeof(InvokeUtil);
            var util = (InvokeUtil)appDomain.CreateInstanceAndUnwrap(type.Assembly.FullName, type.FullName);
            CancellationToken.Register(() => util.Cancel());
            util.Invoke(action.Method.DeclaringType.FullName, action.Method.Name, testOutputHelper, state);

        }
        finally
        {
            AppDomain.Unload(appDomain);
        }
#else
        // On .NET Core the analyzers naturally load into child load contexts so no need for complicated
        // marshalling here.
        action(TestOutputHelper, state, CancellationToken);
#endif

    }

    /// <summary>
    /// This tracks assembly loads to make sure that we aren't polluting the main <see cref="AppDomain"/>
    /// When we load analyzers from tests into the main <see cref="AppDomain"/> that potentially pollutes
    /// or interferes with other tests. Instead each test should be loading into their own.
    /// </summary>
    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs e)
    {
        if (e.LoadedAssembly.GetName().Name == "Microsoft.VisualStudio.Debugger.Runtime.NetCoreApp")
        {
            // This assembly is loaded by the debugger and is not a problem
            return;
        }

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

    protected void AddContentToTestArtifacts(string fileName, string content, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(memberName is not null);
        var (memberDir, _) = GetMemberTestArtifactDirectory(memberName);
        var filePath = Path.Combine(memberDir, fileName);
        File.WriteAllText(filePath, content);
    }

    protected void AddFileToTestArtifacts(string filePath, [CallerMemberName] string? memberName = null)
    {
        Debug.Assert(memberName is not null);
        var (memberDir, overwrite) = GetMemberTestArtifactDirectory(memberName);
        TestOutputHelper.WriteLine($"Saving {filePath} to test artifacts dir {memberDir}");
        File.Copy(filePath, Path.Combine(memberDir, Path.GetFileName(filePath)), overwrite);
    }

    private (string MemberDir, bool Overwrite) GetMemberTestArtifactDirectory(string memberName)
    {
        // Need to overwrite locally or else every time you re-run the test you need to go and 
        // delete the test-artifacts directory
        var overwrite = !TestUtil.InGitHubActions;
        var testResultsDir = TestUtil.TestArtifactsDirectory;

        var typeName = this.GetType().FullName;
        var memberDir = Path.Combine(testResultsDir, $"{typeName}.{memberName}");
        Directory.CreateDirectory(memberDir);
        return (memberDir, overwrite);
    }
}
