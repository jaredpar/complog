using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class PathMappingUtilTests
{
    [Fact]
    public void CryptoKeyFile()
    {
        using var tempDir = new TempDir();
        using var state = new LogReaderState(tempDir.DirectoryPath);
        var pathMappingUtil = PathMappingUtil.CreateDefault(state);
        var path = Path.Combine(tempDir.DirectoryPath, "dir", "key.snk");
        var collisionPath = Path.Combine(tempDir.DirectoryPath, "other", "key.snk");

        Assert.False(pathMappingUtil.IsEmpty);

        var mappedPath = pathMappingUtil.MapPath(path, RawContentKind.CryptoKeyFile);

        Assert.Equal(Path.Combine(state.CryptoKeyFileDirectory, "key.snk"), mappedPath);
        foreach (PathMapKind kind in Enum.GetValues(typeof(PathMapKind)))
        {
            Assert.Equal(path, pathMappingUtil.MapPath(path, kind));
        }

        Assert.Equal(mappedPath, pathMappingUtil.MapPath(path, "keyfile"));

        var collisionMappedPath = pathMappingUtil.MapPath(collisionPath, "keyfile");
        Assert.Equal(Path.Combine(state.CryptoKeyFileDirectory, "key.1.snk"), collisionMappedPath);
        Assert.Equal(
            collisionPath,
            pathMappingUtil.MapPath(collisionPath, PathMapKind.ProjectFile));
        Assert.Equal(
            collisionMappedPath,
            pathMappingUtil.MapPath(collisionPath, RawContentKind.CryptoKeyFile));
    }
}
