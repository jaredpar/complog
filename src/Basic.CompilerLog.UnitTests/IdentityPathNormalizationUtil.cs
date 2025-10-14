using System.Diagnostics.CodeAnalysis;
using Basic.CompilerLog.Util;

namespace Basic.CompilerLog.UnitTests;

/// <summary>
/// This is a no-op path normalizer but it being a custom type means the reader avoids a lot of
/// optimizations it would otherwise hit.
/// </summary>
internal sealed class IdentityPathNormalizationUtil : PathNormalizationUtil
{
    internal override bool IsPathRooted([NotNullWhen(true)] string? path) => Empty.IsPathRooted(path);
    internal override string RootFileName(string fileName) => Empty.RootFileName(fileName);
    internal override string? NormalizePath(string? path) => Empty.NormalizePath(path);
}
