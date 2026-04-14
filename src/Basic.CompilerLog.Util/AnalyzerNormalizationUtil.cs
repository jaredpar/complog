using System.Reflection.PortableExecutable;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Normalizes analyzer assembly bytes by optionally stripping ReadyToRun (R2R) native code.
/// Stripped results are cached so that each assembly is stripped at most once regardless of
/// how many compilations share it. The cache key includes both the assembly MVID and its
/// <see cref="Machine"/> architecture so that AMD64 and ARM64 R2R images with identical
/// MVIDs are cached independently.
/// </summary>
internal abstract class AnalyzerNormalizationUtil
{
    private readonly Dictionary<(Guid Mvid, Machine Architecture), byte[]> _cache = new();

    /// <summary>
    /// Creates the appropriate <see cref="AnalyzerNormalizationUtil"/> for the given strip setting.
    /// </summary>
    /// <param name="stripSetting">
    /// <see langword="null"/> (default): strip only when the assembly targets a different architecture
    /// than the current process. <see langword="true"/>: always strip. <see langword="false"/>: never strip.
    /// </param>
    internal static AnalyzerNormalizationUtil Create(bool? stripSetting) => stripSetting switch
    {
        null => new DefaultAnalyzerNormalizationUtil(),
        true => new AlwaysAnalyzerNormalizationUtil(),
        false => NeverAnalyzerNormalizationUtil.Instance,
    };

    /// <summary>
    /// Determines whether the assembly represented by <paramref name="peReader"/> needs to be
    /// stripped of R2R native code.
    /// </summary>
    internal abstract bool NeedsStripping(PEReader peReader);

    /// <summary>
    /// Returns the normalized bytes for the assembly identified by <paramref name="mvid"/>. When
    /// <see cref="NeedsStripping"/> returns <see langword="false"/> for the given bytes, they are
    /// returned unchanged. When stripping is needed the result is cached so subsequent calls
    /// return the same stripped bytes.
    /// </summary>
    internal virtual byte[] NormalizeBytes(Guid mvid, byte[] bytes)
    {
        using var stream = bytes.AsSimpleMemoryStream(writable: false);
        using var peReader = new PEReader(stream);

        var machine = peReader.PEHeaders.CoffHeader.Machine;
        var key = (mvid, machine);
        if (_cache.TryGetValue(key, out var cachedBytes))
        {
            return cachedBytes;
        }

        if (!NeedsStripping(peReader))
        {
            // Do not cache non-stripped bytes: the IL and R2R versions of the same DLL share the
            // same MVID. Caching the first result would make subsequent lookups return whichever
            // version (R2R or IL) happened to arrive first, causing non-deterministic behavior.
            return bytes;
        }

        var strippedBytes = R2RUtil.StripReadyToRun(peReader);
        _cache[key] = strippedBytes;
        return strippedBytes;
    }
}

file sealed class DefaultAnalyzerNormalizationUtil : AnalyzerNormalizationUtil
{
    internal override bool NeedsStripping(PEReader peReader) => R2RUtil.NeedsStripping(peReader);
}

file sealed class AlwaysAnalyzerNormalizationUtil : AnalyzerNormalizationUtil
{
    internal override bool NeedsStripping(PEReader peReader) => R2RUtil.IsReadyToRun(peReader);
}

file sealed class NeverAnalyzerNormalizationUtil : AnalyzerNormalizationUtil
{
    internal static NeverAnalyzerNormalizationUtil Instance { get; } = new();

    internal override bool NeedsStripping(PEReader peReader) => false;

    internal override byte[] NormalizeBytes(Guid mvid, byte[] bytes) => bytes;
}
