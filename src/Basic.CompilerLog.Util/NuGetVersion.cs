using System.Diagnostics.CodeAnalysis;

namespace Basic.CompilerLog.Util;

/// <summary>
/// Represents a NuGet semantic version number following the format: major.minor.patch[-prerelease]
/// </summary>
public sealed class NuGetVersion : IComparable<NuGetVersion>, IEquatable<NuGetVersion>
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

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetVersion"/> class with major, minor, and patch versions.
    /// </summary>
    public NuGetVersion(int major, int minor, int patch)
        : this(major, minor, patch, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetVersion"/> class with major, minor, patch, and prerelease versions.
    /// </summary>
    public NuGetVersion(int major, int minor, int patch, string? prerelease)
    {
        if (major < 0) throw new ArgumentOutOfRangeException(nameof(major), "Major version must be non-negative");
        if (minor < 0) throw new ArgumentOutOfRangeException(nameof(minor), "Minor version must be non-negative");
        if (patch < 0) throw new ArgumentOutOfRangeException(nameof(patch), "Patch version must be non-negative");

        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrWhiteSpace(prerelease) ? null : prerelease;
    }

    /// <summary>
    /// Attempts to parse a version string in the format "major[.minor[.patch]][-prerelease]"
    /// Missing components default to 0 (e.g., "1" → "1.0.0", "1.2" → "1.2.0")
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out NuGetVersion? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Split on '-' to separate version from prerelease
        var dashIndex = value.IndexOf('-');
        string versionPart;
        string? prereleasePart = null;

        if (dashIndex >= 0)
        {
            versionPart = value.Substring(0, dashIndex);
            prereleasePart = dashIndex + 1 < value.Length ? value.Substring(dashIndex + 1) : null;
        }
        else
        {
            versionPart = value;
        }

        // Parse the version components (major.minor.patch)
        // Allow 1-3 components, defaulting missing ones to 0
        var parts = versionPart.Split('.');
        if (parts.Length < 1 || parts.Length > 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || major < 0)
        {
            return false;
        }

        int minor = 0;
        if (parts.Length > 1)
        {
            if (!int.TryParse(parts[1], out minor) || minor < 0)
            {
                return false;
            }
        }

        int patch = 0;
        if (parts.Length > 2)
        {
            if (!int.TryParse(parts[2], out patch) || patch < 0)
            {
                return false;
            }
        }

        version = new NuGetVersion(major, minor, patch, prereleasePart);
        return true;
    }

    /// <summary>
    /// Compares two NuGetVersion objects.
    /// Versions are compared by major, minor, patch, then prerelease.
    /// Stable versions (no prerelease) are considered greater than prerelease versions.
    /// </summary>
    public int CompareTo(NuGetVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        // Compare major.minor.patch
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;

        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        // If major.minor.patch are equal, compare prerelease
        // Stable versions (no prerelease) > prerelease versions
        if (Prerelease is null && other.Prerelease is null)
        {
            return 0;
        }
        else if (Prerelease is null)
        {
            // This version is stable, other is prerelease
            return 1;
        }
        else if (other.Prerelease is null)
        {
            // This version is prerelease, other is stable
            return -1;
        }

        // Both have prerelease labels, compare them
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>
    /// Compares two prerelease labels using NuGet's prerelease comparison rules.
    /// Prerelease labels are split by '.' and compared component by component.
    /// Numeric components are compared numerically, otherwise lexicographically.
    /// </summary>
    private static int ComparePrerelease(string prerelease1, string prerelease2)
    {
        var parts1 = prerelease1.Split('.');
        var parts2 = prerelease2.Split('.');

        var minLength = Math.Min(parts1.Length, parts2.Length);

        for (int i = 0; i < minLength; i++)
        {
            var part1 = parts1[i];
            var part2 = parts2[i];

            var isNum1 = int.TryParse(part1, out var num1);
            var isNum2 = int.TryParse(part2, out var num2);

            if (isNum1 && isNum2)
            {
                // Both are numeric, compare numerically
                var result = num1.CompareTo(num2);
                if (result != 0) return result;
            }
            else if (isNum1)
            {
                // Numbers are less than alphanumeric strings
                return -1;
            }
            else if (isNum2)
            {
                // Alphanumeric strings are greater than numbers
                return 1;
            }
            else
            {
                // Both are alphanumeric, compare lexicographically
                var result = string.Compare(part1, part2, StringComparison.OrdinalIgnoreCase);
                if (result != 0) return result;
            }
        }

        // If all compared parts are equal, the shorter prerelease is less
        return parts1.Length.CompareTo(parts2.Length);
    }

    public bool Equals(NuGetVersion? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Major == other.Major &&
               Minor == other.Minor &&
               Patch == other.Patch &&
               string.Equals(Prerelease, other.Prerelease, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as NuGetVersion);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        hash.Add(Prerelease?.ToUpperInvariant());
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";
    }

    public static bool operator ==(NuGetVersion? left, NuGetVersion? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(NuGetVersion? left, NuGetVersion? right) => !(left == right);

    public static bool operator <(NuGetVersion? left, NuGetVersion? right)
    {
        if (left is null) return right is not null;
        return left.CompareTo(right) < 0;
    }

    public static bool operator <=(NuGetVersion? left, NuGetVersion? right)
    {
        if (left is null) return true;
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >(NuGetVersion? left, NuGetVersion? right)
    {
        if (left is null) return false;
        return left.CompareTo(right) > 0;
    }

    public static bool operator >=(NuGetVersion? left, NuGetVersion? right)
    {
        if (left is null) return right is null;
        return left.CompareTo(right) >= 0;
    }
}
