using System.Reflection.PortableExecutable;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Caches the (possibly stripped) bytes of ReadyToRun analyzer assemblies so that
/// each assembly is stripped at most once regardless of how many compilations share it.
/// The cache key is a tuple of the assembly MVID and its <see cref="Machine"/> architecture
/// so that AMD64 and ARM64 R2R images with identical MVIDs are cached independently
/// (stripping different architectures may yield different IL bytes).
/// </summary>
internal sealed class AnalyzerByteCache
{
    private readonly Dictionary<(Guid Mvid, Machine Architecture), byte[]> _cache = new();

    /// <summary>
    /// Returns the bytes for the assembly identified by <paramref name="mvid"/>, applying the
    /// <paramref name="stripSetting"/> policy and caching the result.
    /// </summary>
    /// <param name="mvid">The assembly MVID.</param>
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
    /// Invoked to read the raw assembly bytes from disk or from an archive entry.
    /// </param>
    internal byte[] GetOrStrip(Guid mvid, bool? stripSetting, Func<byte[]> getBytesFunc)
    {
        var rawBytes = getBytesFunc();

        // Read the machine type from the PE header to form a complete cache key.
        // AMD64 and ARM64 R2R images may share the same MVID yet yield different IL bytes
        // after stripping, so both MVID and architecture are required.
        Machine machine;
        using (var stream = rawBytes.AsSimpleMemoryStream(writable: false))
        using (var peReader = new PEReader(stream))
        {
            machine = peReader.PEHeaders.CoffHeader.Machine;
        }

        var key = (mvid, machine);
        if (_cache.TryGetValue(key, out var cachedBytes))
        {
            return cachedBytes;
        }

        bool needsStrip = stripSetting switch
        {
            true => R2RUtil.IsReadyToRun(rawBytes),
            false => false,
            null => R2RUtil.NeedsStripping(rawBytes),
        };

        if (!needsStrip)
        {
            // Do not cache non-stripped bytes: the IL and R2R versions of the same DLL share the
            // same MVID. Caching the first result would make subsequent lookups return whichever
            // version (R2R or IL) happened to arrive first, causing non-deterministic behavior.
            return rawBytes;
        }

        var strippedBytes = R2RUtil.StripReadyToRun(rawBytes);
        _cache[key] = strippedBytes;
        return strippedBytes;
    }
}
