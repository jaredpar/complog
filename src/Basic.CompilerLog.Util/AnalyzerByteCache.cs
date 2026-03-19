
namespace Basic.CompilerLog.Util;

/// <summary>
/// Caches the (possibly stripped) bytes of ReadyToRun analyzer assemblies so that
/// each assembly is stripped at most once regardless of how many compilations share it.
/// </summary>
internal sealed class AnalyzerByteCache
{
    private readonly Dictionary<Guid, byte[]> _cache = new();

    /// <summary>
    /// Returns the bytes for the assembly identified by <paramref name="mvid"/>, applying the
    /// <paramref name="stripSetting"/> policy and caching the result.
    /// </summary>
    /// <param name="mvid">The assembly MVID used as the cache key.</param>
    /// <param name="stripSetting">
    /// Controls stripping of ReadyToRun native code:
    /// <list type="bullet">
    ///   <item><description><see langword="null"/> (default): strip only when the assembly targets a
    ///     different architecture than the current process.</description></item>
    ///   <item><description><see langword="true"/>: always strip.</description></item>
    ///   <item><description><see langword="false"/>: never strip.</description></item>
    /// </list>
    /// </param>
    /// <param name="getBytesFunc">
    /// Invoked to read the raw assembly bytes from disk or from an archive entry. Only called
    /// when the result is not already in the cache.
    /// </param>
    internal byte[] GetOrStrip(Guid mvid, bool? stripSetting, Func<byte[]> getBytesFunc)
    {
        if (_cache.TryGetValue(mvid, out var cachedBytes))
        {
            return cachedBytes;
        }

        var rawBytes = getBytesFunc();
        bool needsStrip = stripSetting switch
        {
            true => R2RUtil.IsReadyToRun(rawBytes),
            false => false,
            null => R2RUtil.NeedsStripping(rawBytes),
        };

        if (!needsStrip)
        {
            _cache[mvid] = rawBytes;
            return rawBytes;
        }

        var strippedBytes = R2RUtil.StripReadyToRun(rawBytes);
        _cache[mvid] = strippedBytes;
        return strippedBytes;
    }
}
