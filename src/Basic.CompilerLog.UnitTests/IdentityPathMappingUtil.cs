using Basic.CompilerLog.Util;

namespace Basic.CompilerLog.UnitTests;

/// <summary>
/// This is a no-op path mapper but it being a custom type means the reader avoids a lot of
/// optimizations it would otherwise hit.
/// </summary>
internal sealed class IdentityPathMappingUtil : PathMappingUtil
{
    internal override bool IsEmpty => false;
}
