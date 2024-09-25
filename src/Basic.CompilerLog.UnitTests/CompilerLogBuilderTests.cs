using Basic.CompilerLog.Util;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(SolutionFixtureCollection.Name)]
public sealed class CompilerLogBuilderTests : TestBase
{
    public SolutionFixture Fixture { get; }

    public CompilerLogBuilderTests(ITestOutputHelper testOutputHelper, SolutionFixture fixture)
        : base(testOutputHelper, nameof(CompilerLogBuilderTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void AddMissingFile()
    {
        using var stream = new MemoryStream();
        using var builder = new CompilerLogBuilder(stream, new());
        using var binlogStream = new FileStream(Fixture.ConsoleWithDiagnosticsBinaryLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var compilerCall = BinaryLogUtil.ReadAllCompilerCalls(binlogStream).First(x => x.IsCSharp);
        compilerCall = compilerCall.WithArguments(["/sourcelink:does-not-exist.txt"]);
        Assert.Throws<Exception>(() => builder.AddFromDisk(compilerCall, BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall)));
    }

    [Fact]
    public void AddWithMissingCompilerFilePath()
    {
        using var stream = new MemoryStream();
        using var builder = new CompilerLogBuilder(stream, new());
        using var binlogStream = new FileStream(Fixture.ConsoleWithDiagnosticsBinaryLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var compilerCall = BinaryLogUtil.ReadAllCompilerCalls(binlogStream).First(x => x.IsCSharp);
        var args = compilerCall.GetArguments();
        compilerCall = new CompilerCall(
            compilerFilePath: null,
            compilerCall.ProjectFilePath,
            CompilerCallKind.Regular,
            compilerCall.TargetFramework,
            isCSharp: true,
            new Lazy<IReadOnlyCollection<string>>(() => args),
            null);
        builder.AddFromDisk(compilerCall, BinaryLogUtil.ReadCommandLineArgumentsUnsafe(compilerCall));
    }

    [Fact]
    public void PortablePdbMissing()
    {
        RunDotNet("new console -o .");
        RunDotNet("build -bl:msbuild.binlog");

        Directory
            .EnumerateFiles(RootDirectory, "*.pdb", SearchOption.AllDirectories)
            .ForEach(File.Delete);

        using var complogStream = new MemoryStream();
        using var binlogStream = new FileStream(Path.Combine(RootDirectory, "msbuild.binlog"), FileMode.Open, FileAccess.Read, FileShare.Read); 
        var diagnostics = CompilerLogUtil.ConvertBinaryLog(binlogStream, complogStream);
        Assert.Contains(diagnostics, x => x.Contains("Can't find portable pdb"));
    }

    [Fact]
    public void CloseTwice()
    {
        var builder = new CompilerLogBuilder(new MemoryStream(), []);
        builder.Close();
        Assert.Throws<InvalidOperationException>(() => builder.Close());
    }
}