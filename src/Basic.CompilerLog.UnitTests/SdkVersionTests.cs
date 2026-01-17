using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class SdkVersionTests
{
    [Fact]
    public void Constructor_ValidVersions_Succeeds()
    {
        var v1 = new SdkVersion(1, 2, 3);
        Assert.Equal(1, v1.Major);
        Assert.Equal(2, v1.Minor);
        Assert.Equal(3, v1.Patch);
        Assert.Null(v1.Prerelease);

        var v2 = new SdkVersion(5, 0, 100, "rc.2");
        Assert.Equal(5, v2.Major);
        Assert.Equal(0, v2.Minor);
        Assert.Equal(100, v2.Patch);
        Assert.Equal("rc.2", v2.Prerelease);
    }

    [Fact]
    public void Constructor_NegativeMajor_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SdkVersion(-1, 0, 0));
    }

    [Fact]
    public void Constructor_NegativeMinor_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SdkVersion(0, -1, 0));
    }

    [Fact]
    public void Constructor_NegativePatch_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SdkVersion(0, 0, -1));
    }

    [Fact]
    public void Constructor_EmptyPrereleaseString_TreatsAsNull()
    {
        var v1 = new SdkVersion(1, 0, 0, "");
        Assert.Null(v1.Prerelease);

        var v2 = new SdkVersion(1, 0, 0, "   ");
        Assert.Null(v2.Prerelease);
    }

    [Theory]
    [InlineData("1.0.0", 1, 0, 0, null)]
    [InlineData("1.0", 1, 0, 0, null)]             // Missing patch defaults to 0
    [InlineData("1", 1, 0, 0, null)]               // Missing minor and patch default to 0
    [InlineData("9.0.100", 9, 0, 100, null)]
    [InlineData("10.0.100", 10, 0, 100, null)]
    [InlineData("1.2.3-alpha", 1, 2, 3, "alpha")]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [InlineData("10.0.100-rc.2.25502.107", 10, 0, 100, "rc.2.25502.107")]
    [InlineData("2.0.0-preview.1.2.3", 2, 0, 0, "preview.1.2.3")]
    [InlineData("1.0.0-alpha.beta.gamma", 1, 0, 0, "alpha.beta.gamma")]
    [InlineData("5.4.3-RC", 5, 4, 3, "RC")]
    [InlineData("0.0.0", 0, 0, 0, null)]
    [InlineData("0", 0, 0, 0, null)]               // Zero version with defaults
    [InlineData("5.2", 5, 2, 0, null)]             // Two components
    [InlineData("3-alpha", 3, 0, 0, "alpha")]      // One component with prerelease
    [InlineData("2.1-beta", 2, 1, 0, "beta")]      // Two components with prerelease
    public void TryParse_ValidVersionStrings_ReturnsTrue(string input, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(SdkVersion.TryParse(input, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a.b.c")]                  // Non-numeric components
    [InlineData("1.2.c")]                  // Non-numeric patch
    [InlineData("1.b.3")]                  // Non-numeric minor
    [InlineData("1.b")]                    // Non-numeric minor (two components)
    [InlineData("a.2.3")]                  // Non-numeric major
    [InlineData("a")]                      // Non-numeric major (one component)
    [InlineData("-1.0.0")]                 // Negative major
    [InlineData("1.-1.0")]                 // Negative minor
    [InlineData("1.0.-1")]                 // Negative patch
    [InlineData("not.a.version")]          // Invalid format
    [InlineData("1.2.3.4")]                // Too many version components
    [InlineData("1.2.3.4.5")]              // Too many version components
    public void TryParse_InvalidVersionStrings_ReturnsFalse(string? input)
    {
        Assert.False(SdkVersion.TryParse(input, out var version));
    }

    [Fact]
    public void TryParse_VersionWithTrailingDashButNoPrerelease_ReturnsTrueWithNullPrerelease()
    {
        // This is a bit ambiguous, but based on the implementation, "1.0.0-" should parse
        // with an empty prerelease which becomes null
        Assert.True(SdkVersion.TryParse("1.0.0-", out var version));
        Assert.Null(version.Prerelease);
    }

    [Fact]
    public void TryParse_FlexibleVersionFormats_ParsesCorrectly()
    {
        // Test that missing components default to 0
        Assert.True(SdkVersion.TryParse("7", out var v1));
        Assert.Equal(new SdkVersion(7, 0, 0), v1);

        Assert.True(SdkVersion.TryParse("7.5", out var v2));
        Assert.Equal(new SdkVersion(7, 5, 0), v2);

        Assert.True(SdkVersion.TryParse("7.5.3", out var v3));
        Assert.Equal(new SdkVersion(7, 5, 3), v3);
    }

    [Fact]
    public void CompareTo_SameVersions_ReturnsZero()
    {
        var v1 = new SdkVersion(1, 2, 3);
        var v2 = new SdkVersion(1, 2, 3);
        Assert.Equal(0, v1.CompareTo(v2));
    }

    [Fact]
    public void CompareTo_DifferentMajor_ComparesCorrectly()
    {
        var v1 = new SdkVersion(1, 0, 0);
        var v2 = new SdkVersion(2, 0, 0);
        Assert.True(v1.CompareTo(v2) < 0);
        Assert.True(v2.CompareTo(v1) > 0);
    }

    [Fact]
    public void CompareTo_DifferentMinor_ComparesCorrectly()
    {
        var v1 = new SdkVersion(1, 0, 0);
        var v2 = new SdkVersion(1, 1, 0);
        Assert.True(v1.CompareTo(v2) < 0);
        Assert.True(v2.CompareTo(v1) > 0);
    }

    [Fact]
    public void CompareTo_DifferentPatch_ComparesCorrectly()
    {
        var v1 = new SdkVersion(1, 0, 0);
        var v2 = new SdkVersion(1, 0, 1);
        Assert.True(v1.CompareTo(v2) < 0);
        Assert.True(v2.CompareTo(v1) > 0);
    }

    [Fact]
    public void CompareTo_StableVsPrerelease_StableIsGreater()
    {
        var stable = new SdkVersion(1, 0, 0);
        var prerelease = new SdkVersion(1, 0, 0, "alpha");
        Assert.True(stable.CompareTo(prerelease) > 0);
        Assert.True(prerelease.CompareTo(stable) < 0);
    }

    [Fact]
    public void CompareTo_PrereleaseVersions_ComparesCorrectly()
    {
        var v1 = new SdkVersion(1, 0, 0, "alpha");
        var v2 = new SdkVersion(1, 0, 0, "beta");
        Assert.True(v1.CompareTo(v2) < 0);

        var v3 = new SdkVersion(1, 0, 0, "rc.1");
        var v4 = new SdkVersion(1, 0, 0, "rc.2");
        Assert.True(v3.CompareTo(v4) < 0);
    }

    [Fact]
    public void CompareTo_MixedNumericAndAlphaPrerelease_NumericIsLess()
    {
        var numeric = new SdkVersion(1, 0, 0, "1");
        var alpha = new SdkVersion(1, 0, 0, "alpha");
        // Numeric components are less than alphanumeric
        Assert.True(numeric.CompareTo(alpha) < 0);
    }

    [Fact]
    public void CompareTo_DifferentPrereleaseLengths_ShorterIsLess()
    {
        var v1 = new SdkVersion(1, 0, 0, "rc");
        var v2 = new SdkVersion(1, 0, 0, "rc.1");
        // When all compared parts are equal, shorter is less
        Assert.True(v1.CompareTo(v2) < 0);
    }

    [Fact]
    public void CompareTo_RealWorldExample_CorrectOrdering()
    {
        // From the actual test case
        var v9 = new SdkVersion(9, 0, 100);
        var v10rc = new SdkVersion(10, 0, 100, "rc.2.25502.107");

        Assert.True(v9.CompareTo(v10rc) < 0);
        Assert.True(v10rc.CompareTo(v9) > 0);
    }

    [Fact]
    public void Equals_SameVersions_ReturnsTrue()
    {
        var v1 = new SdkVersion(1, 2, 3);
        var v2 = new SdkVersion(1, 2, 3);
        Assert.True(v1.Equals(v2));
        Assert.True(v1 == v2);
        Assert.False(v1 != v2);
    }

    [Fact]
    public void Equals_SameVersionsWithPrerelease_ReturnsTrue()
    {
        var v1 = new SdkVersion(1, 2, 3, "alpha");
        var v2 = new SdkVersion(1, 2, 3, "alpha");
        Assert.True(v1.Equals(v2));
        Assert.True(v1 == v2);
    }

    [Fact]
    public void Equals_DifferentVersions_ReturnsFalse()
    {
        var v1 = new SdkVersion(1, 2, 3);
        var v2 = new SdkVersion(1, 2, 4);
        Assert.False(v1.Equals(v2));
        Assert.True(v1 != v2);
    }

    [Fact]
    public void Equals_DifferentPrerelease_ReturnsFalse()
    {
        var v1 = new SdkVersion(1, 2, 3, "alpha");
        var v2 = new SdkVersion(1, 2, 3, "beta");
        Assert.False(v1.Equals(v2));
    }

    [Fact]
    public void Equals_StableVsPrerelease_ReturnsFalse()
    {
        var stable = new SdkVersion(1, 2, 3);
        var prerelease = new SdkVersion(1, 2, 3, "alpha");
        Assert.False(stable.Equals(prerelease));
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var v = new SdkVersion(1, 0, 0);
        Assert.False(v.Equals(null));
    }

    [Fact]
    public void Equals_SameInstance_ReturnsTrue()
    {
        var v = new SdkVersion(1, 0, 0);
        Assert.True(v.Equals(v));
        #pragma warning disable CS1718 // Comparison made to same variable
        Assert.True(v == v);
        #pragma warning restore CS1718 // Comparison made to same variable
    }

    [Fact]
    public void Equals_CaseInsensitivePrerelease_ReturnsTrue()
    {
        var v1 = new SdkVersion(1, 0, 0, "RC");
        var v2 = new SdkVersion(1, 0, 0, "rc");
        Assert.True(v1.Equals(v2));
    }

    [Fact]
    public void GetHashCode_EqualVersions_ReturnsSameHashCode()
    {
        var v1 = new SdkVersion(1, 2, 3, "alpha");
        var v2 = new SdkVersion(1, 2, 3, "alpha");
        Assert.Equal(v1.GetHashCode(), v2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_CaseInsensitivePrerelease_ReturnsSameHashCode()
    {
        var v1 = new SdkVersion(1, 0, 0, "RC");
        var v2 = new SdkVersion(1, 0, 0, "rc");
        Assert.Equal(v1.GetHashCode(), v2.GetHashCode());
    }

    [Fact]
    public void ToString_StableVersion_FormatsCorrectly()
    {
        var v = new SdkVersion(1, 2, 3);
        Assert.Equal("1.2.3", v.ToString());
    }

    [Fact]
    public void ToString_PrereleaseVersion_FormatsCorrectly()
    {
        var v = new SdkVersion(1, 2, 3, "alpha");
        Assert.Equal("1.2.3-alpha", v.ToString());
    }

    [Fact]
    public void ToString_ComplexPrereleaseVersion_FormatsCorrectly()
    {
        var v = new SdkVersion(10, 0, 100, "rc.2.25502.107");
        Assert.Equal("10.0.100-rc.2.25502.107", v.ToString());
    }

    [Fact]
    public void Sorting_MultipleVersions_OrdersCorrectly()
    {
        var versions = new List<SdkVersion>
        {
            new SdkVersion(2, 0, 0),
            new SdkVersion(1, 0, 0, "alpha"),
            new SdkVersion(1, 0, 0),
            new SdkVersion(1, 0, 0, "beta"),
            new SdkVersion(1, 1, 0),
            new SdkVersion(1, 0, 1),
        };

        var sorted = versions.OrderBy(v => v).ToList();

        Assert.Equal(new SdkVersion(1, 0, 0, "alpha"), sorted[0]);
        Assert.Equal(new SdkVersion(1, 0, 0, "beta"), sorted[1]);
        Assert.Equal(new SdkVersion(1, 0, 0), sorted[2]);
        Assert.Equal(new SdkVersion(1, 0, 1), sorted[3]);
        Assert.Equal(new SdkVersion(1, 1, 0), sorted[4]);
        Assert.Equal(new SdkVersion(2, 0, 0), sorted[5]);
    }

    [Fact]
    public void Sorting_RealWorldSdkVersions_OrdersCorrectly()
    {
        var versions = new List<SdkVersion>
        {
            new SdkVersion(10, 0, 100, "rc.2.25502.107"),
            new SdkVersion(9, 0, 100),
            new SdkVersion(8, 0, 303),
            new SdkVersion(10, 0, 100, "rc.1.25452.101"),
            new SdkVersion(10, 0, 100),
        };

        var sorted = versions.OrderBy(v => v).ToList();

        Assert.Equal("8.0.303", sorted[0].ToString());
        Assert.Equal("9.0.100", sorted[1].ToString());
        Assert.Equal("10.0.100-rc.1.25452.101", sorted[2].ToString());
        Assert.Equal("10.0.100-rc.2.25502.107", sorted[3].ToString());
        Assert.Equal("10.0.100", sorted[4].ToString());
    }

    [Fact]
    public void OrderByDescending_ReturnsLatestVersion()
    {
        var versions = new List<SdkVersion>
        {
            new SdkVersion(9, 0, 100),
            new SdkVersion(10, 0, 100, "rc.2.25502.107"),
            new SdkVersion(8, 0, 303),
        };

        var latest = versions.OrderByDescending(v => v).First();
        Assert.Equal(new SdkVersion(10, 0, 100, "rc.2.25502.107"), latest);
    }

    [Fact]
    public void EdgeCase_ZeroVersion_WorksCorrectly()
    {
        var v = new SdkVersion(0, 0, 0);
        Assert.Equal("0.0.0", v.ToString());
    }

    [Fact]
    public void EdgeCase_LargeVersionNumbers_WorksCorrectly()
    {
        var v = new SdkVersion(999, 999, 999);
        Assert.Equal("999.999.999", v.ToString());
        Assert.True(SdkVersion.TryParse("999.999.999", out _));
    }

    [Fact]
    public void EdgeCase_ComplexPrereleaseLabels_ParseCorrectly()
    {
        Assert.True(SdkVersion.TryParse("1.0.0-alpha.1.2.3.4.5", out var v));
        Assert.Equal("alpha.1.2.3.4.5", v!.Prerelease);
    }
}
