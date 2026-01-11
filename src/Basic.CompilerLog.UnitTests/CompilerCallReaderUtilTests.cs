using System.IO.Compression;
using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class CompilerCallReaderUtilTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public CompilerCallReaderUtilTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor, CompilerLogFixture fixture)
        : base(testOutputHelper, testContextAccessor, nameof(CompilerLogReaderTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void CreateBadExtension()
    {
        Assert.Throws<ArgumentException>(() => CompilerCallReaderUtil.Create("file.bad"));
    }

    [Fact]
    public void CreateFromZip()
    {
        Go(Fixture.Console.Value.CompilerLogPath, "build.complog");
        Go(Fixture.Console.Value.BinaryLogPath!, "build.binlog");

        void Go(string filePath, string entryName)
        {
            var d = Root.NewDirectory();
            var zipFilePath = Path.Combine(d, "file.zip");
            CreateZip(zipFilePath, filePath, entryName);

            using var reader = CompilerCallReaderUtil.Create(zipFilePath);
            var compilerCalls = reader.ReadAllCompilerCalls();
            Assert.NotEmpty(compilerCalls);
        }

        void CreateZip(string zipFilePath, string logFilePath, string entryName)
        {
            using var fileStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(logFilePath, entryName);
        }
    }


    [Fact]
    public void CreateFromZipRenamedComplog()
    {
        // Test for issue #307: support .complog files renamed to .zip extension
        var d = Root.NewDirectory();
        var zipFilePath = Path.Combine(d, "build.zip");

        // Copy the .complog file to a .zip extension (simulating a rename)
        File.Copy(Fixture.Console.Value.CompilerLogPath, zipFilePath);

        // Should be able to open it directly
        using var reader = CompilerCallReaderUtil.Create(zipFilePath);
        var compilerCalls = reader.ReadAllCompilerCalls();
        Assert.NotEmpty(compilerCalls);
    }

    [Theory]
    [MemberData(nameof(GetBasicAnalyzerKinds))]
    public void GetAllAnalyzerKinds(BasicAnalyzerKind basicAnalyzerKind)
    {
        using var reader = CompilerCallReaderUtil.Create(Fixture.Console.Value.CompilerLogPath!, basicAnalyzerKind);
        Assert.Equal(basicAnalyzerKind, reader.BasicAnalyzerKind);
    }
}