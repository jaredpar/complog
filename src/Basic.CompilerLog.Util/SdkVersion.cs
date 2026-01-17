using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Represents a NuGet semantic version number following the format: major.minor.patch[-prerelease]
/// </summary>
public readonly struct SdkVersion : IComparable<SdkVersion>, IEquatable<SdkVersion>
{
    /// <summary>
    /// Gets the major version number (X.0.0)
    /// </summary>
    public int Major { get; }

    /// <summary>
    /// Gets the minor version number (0.X.0)
    /// </summary>
    public int Minor { get; }

    /// <summary>
    /// Gets the patch version number (0.0.X)
    /// </summary>
    public int Patch { get; }

    /// <summary>
    /// Gets the prerelease label (e.g., "rc.2.25502.107" in "10.0.100-rc.2.25502.107")
    /// </summary>
    public string? Prerelease { get; }

    public SdkVersion(int major, int minor, int patch, string? prerelease = null)
    {
        if (major < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), "Major version must be non-negative");
        }

        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minor), "Minor version must be non-negative");
        }

        if (patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(patch), "Patch version must be non-negative");
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrWhiteSpace(prerelease) ? null : prerelease;
    }

    /// <summary>
    /// Attempts to parse a version string in the format "major[.minor[.patch]][-prerelease]"
    /// Missing components default to 0 (e.g., "1" → "1.0.0", "1.2" → "1.2.0")
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> value, [NotNullWhen(true)] out SdkVersion version)
    {
        version = default;

        if (value.Length == 0)
        {
            return false;
        }

        // Split on '-' to separate version from prerelease
        var dashIndex = value.IndexOf('-');
        ReadOnlySpan<char> versionPart;
        ReadOnlySpan<char> prereleasePart;

        if (dashIndex >= 0)
        {
            versionPart = value.Slice(0, dashIndex);
            prereleasePart = value.Slice(dashIndex + 1);
        }
        else
        {
            versionPart = value;
            prereleasePart = ReadOnlySpan<char>.Empty;
        }

        if (!ParseDigit(ref versionPart, out var major))
        {
            return false;
        }

        var minor = 0;
        var patch = 0;
        if (versionPart.Length > 0 && !ParseDigit(ref versionPart, out minor))
        {
            return false;
        }

        if (versionPart.Length > 0 && !ParseDigit(ref versionPart, out patch))
        {
            return false;
        }

        if (versionPart.Length > 0)
        {
            return false;
        }

        version = new SdkVersion(major, minor, patch, prereleasePart.Length > 0 ? prereleasePart.ToString() : null);
        return true;

        bool ParseDigit(scoped ref ReadOnlySpan<char> span, out int result)
        {
            result = 0;

            var index = 0;
            while (index < span.Length)
            {
                var c = span[index];
                if (c == '.')
                {
                    index++;
                    if (index >= span.Length)
                    {
                        return false;
                    }
                    break;
                }

                if (c < '0' || c > '9')
                {
                    result = 0;
                    return false;
                }

                result = result * 10 + (c - '0');
                index++;
            }

            if (index == 0)
            {
                return false;
            }

            span = span.Slice(index);
            return true;
        }
    }

    public int CompareTo(SdkVersion other)
    {
        // Compare major.minor.patch
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        if (Prerelease is null && other.Prerelease is null)
        {
            return 0;
        }

        if (Prerelease is null)
        {
            return 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        return string.Compare(Prerelease, other.Prerelease, StringComparison.OrdinalIgnoreCase);
    }

    public bool Equals(SdkVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SdkVersion other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Major;
            hash = hash * 31 + Minor;
            hash = hash * 31 + Patch;
            hash = hash * 31 + (Prerelease is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Prerelease));
            return hash;
        }
    }

    public override string ToString() =>
        Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    public static bool operator ==(SdkVersion left, SdkVersion right) => left.Equals(right);

    public static bool operator !=(SdkVersion left, SdkVersion right) => !left.Equals(right);
}
