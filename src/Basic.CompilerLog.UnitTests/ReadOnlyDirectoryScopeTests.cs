using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class ReadOnlyDirectoryScopeTests : IDisposable
{
    internal TempDir Root { get; } = new();

    public void Dispose()
    {
        Root.Dispose();
    }

    [Fact]
    public void CannotAddFiles()
    {
        var dir = Root.NewDirectory();
        using var scope = new ReadOnlyDirectoryScope(dir, setReadOnly: true);
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(dir, "test.txt"), "hello world"));
    }

    [Fact]
    public void CannotModifyFiles()
    {
        var dir = Root.NewDirectory();
        var filePath = Path.Combine(dir, "test.txt");
        File.WriteAllText(filePath, "hello world");
        using var scope = new ReadOnlyDirectoryScope(dir, setReadOnly: true);
        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            using var stream = File.OpenWrite(filePath);
            stream.Write([0x48], 0, 1);
        });
    }

    [Fact]
    public void CannotDeleteFiles()
    {
        var dir = Root.NewDirectory();
        var filePath = Path.Combine(dir, "test.txt");
        File.WriteAllText(filePath, "hello world");
        using var scope = new ReadOnlyDirectoryScope(dir, setReadOnly: true);
        Assert.Throws<UnauthorizedAccessException>(() => File.Delete(filePath));
    }

    [Fact]
    public void CanReadFiles()
    {
        var dir = Root.NewDirectory();
        var filePath = Path.Combine(dir, "test.txt");
        var text = "hello world";
        File.WriteAllText(filePath, text);
        using var scope = new ReadOnlyDirectoryScope(dir, setReadOnly: true);
        Assert.Equal(text, File.ReadAllText(filePath));
    }
}
