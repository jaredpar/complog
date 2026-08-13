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
        var secondCollisionPath = Path.Combine(tempDir.DirectoryPath, "another", "key.snk");

        Assert.False(pathMappingUtil.IsEmpty);
        Assert.Null(pathMappingUtil.MapPath(null, RawContentKind.CryptoKeyFile));
        Assert.Equal(path, pathMappingUtil.MapPath(path, RawContentKind.SourceText));
        Assert.Equal(path, pathMappingUtil.MapPath(path, "out"));

        var identityPathMappingUtil = new IdentityPathMappingUtil();
        Assert.Equal(path, identityPathMappingUtil.MapPath(path, RawContentKind.CryptoKeyFile));
        Assert.Equal(path, identityPathMappingUtil.MapPath(path, "keyfile"));

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

        var secondCollisionMappedPath = pathMappingUtil.MapPath(
            secondCollisionPath,
            RawContentKind.CryptoKeyFile);
        Assert.Equal(
            Path.Combine(state.CryptoKeyFileDirectory, "key.2.snk"),
            secondCollisionMappedPath);
    }
}
