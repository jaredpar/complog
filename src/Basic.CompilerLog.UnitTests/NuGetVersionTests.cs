using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class NuGetVersionTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_ValidVersions_Succeeds()
    {
        var v1 = new NuGetVersion(1, 2, 3);
        Assert.Equal(1, v1.Major);
        Assert.Equal(2, v1.Minor);
        Assert.Equal(3, v1.Patch);
        Assert.Null(v1.Prerelease);

        var v2 = new NuGetVersion(5, 0, 100, "rc.2");
        Assert.Equal(5, v2.Major);
        Assert.Equal(0, v2.Minor);
        Assert.Equal(100, v2.Patch);
        Assert.Equal("rc.2", v2.Prerelease);
    }

    [Fact]
    public void Constructor_NegativeMajor_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NuGetVersion(-1, 0, 0));
    }

    [Fact]
    public void Constructor_NegativeMinor_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NuGetVersion(0, -1, 0));
    }

    [Fact]
    public void Constructor_NegativePatch_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NuGetVersion(0, 0, -1));
    }

    [Fact]
    public void Constructor_EmptyPrereleaseString_TreatsAsNull()
    {
        var v1 = new NuGetVersion(1, 0, 0, "");
        Assert.Null(v1.Prerelease);

        var v2 = new NuGetVersion(1, 0, 0, "   ");
        Assert.Null(v2.Prerelease);
    }

    #endregion

    #region TryParse Tests

    [Theory]
    [InlineData("1.0.0", 1, 0, 0, null)]
    [InlineData("9.0.100", 9, 0, 100, null)]
    [InlineData("10.0.100", 10, 0, 100, null)]
    [InlineData("1.2.3-alpha", 1, 2, 3, "alpha")]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [InlineData("10.0.100-rc.2.25502.107", 10, 0, 100, "rc.2.25502.107")]
    [InlineData("2.0.0-preview.1.2.3", 2, 0, 0, "preview.1.2.3")]
    [InlineData("1.0.0-alpha.beta.gamma", 1, 0, 0, "alpha.beta.gamma")]
    [InlineData("5.4.3-RC", 5, 4, 3, "RC")]
    [InlineData("0.0.0", 0, 0, 0, null)]
    public void TryParse_ValidVersionStrings_ReturnsTrue(string input, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(NuGetVersion.TryParse(input, out var version));
        Assert.NotNull(version);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.0")]                    // Missing patch
    [InlineData("1")]                      // Missing minor and patch
    [InlineData("a.b.c")]                  // Non-numeric components
    [InlineData("1.2.c")]                  // Non-numeric patch
    [InlineData("1.b.3")]                  // Non-numeric minor
    [InlineData("a.2.3")]                  // Non-numeric major
    [InlineData("-1.0.0")]                 // Negative major
    [InlineData("1.-1.0")]                 // Negative minor
    [InlineData("1.0.-1")]                 // Negative patch
    [InlineData("1.0.0-")]                 // Empty prerelease (trailing dash)
    [InlineData("not.a.version")]          // Invalid format
    [InlineData("1.2.3.4")]                // Extra version component (build metadata not supported in this simple implementation)
    public void TryParse_InvalidVersionStrings_ReturnsFalse(string? input)
    {
        Assert.False(NuGetVersion.TryParse(input, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void TryParse_VersionWithTrailingDashButNoPrerelease_ReturnsTrueWithNullPrerelease()
    {
        // This is a bit ambiguous, but based on the implementation, "1.0.0-" should parse
        // with an empty prerelease which becomes null
        Assert.True(NuGetVersion.TryParse("1.0.0-", out var version));
        Assert.NotNull(version);
        Assert.Null(version.Prerelease);
    }

    #endregion

    #region Comparison Tests

    [Fact]
    public void CompareTo_SameVersions_ReturnsZero()
    {
        var v1 = new NuGetVersion(1, 2, 3);
        var v2 = new NuGetVersion(1, 2, 3);
        Assert.Equal(0, v1.CompareTo(v2));
    }

    [Fact]
    public void CompareTo_DifferentMajor_ComparesCorrectly()
    {
        var v1 = new NuGetVersion(1, 0, 0);
        var v2 = new NuGetVersion(2, 0, 0);
        Assert.True(v1.CompareTo(v2) < 0);
        Assert.True(v2.CompareTo(v1) > 0);
    }

    [Fact]
    public void CompareTo_DifferentMinor_ComparesCorrectly()
    {
        var v1 = new NuGetVersion(1, 0, 0);
        var v2 = new NuGetVersion(1, 1, 0);
        Assert.True(v1.CompareTo(v2) < 0);
        Assert.True(v2.CompareTo(v1) > 0);
    }

    [Fact]
    public void CompareTo_DifferentPatch_ComparesCorrectly()
    {
        var v1 = new NuGetVersion(1, 0, 0);
        var v2 = new NuGetVersion(1, 0, 1);
        Assert.True(v1.CompareTo(v2) < 0);
        Assert.True(v2.CompareTo(v1) > 0);
    }

    [Fact]
    public void CompareTo_StableVsPrerelease_StableIsGreater()
    {
        var stable = new NuGetVersion(1, 0, 0);
        var prerelease = new NuGetVersion(1, 0, 0, "alpha");
        Assert.True(stable.CompareTo(prerelease) > 0);
        Assert.True(prerelease.CompareTo(stable) < 0);
    }

    [Fact]
    public void CompareTo_PrereleaseVersions_ComparesCorrectly()
    {
        var v1 = new NuGetVersion(1, 0, 0, "alpha");
        var v2 = new NuGetVersion(1, 0, 0, "beta");
        Assert.True(v1.CompareTo(v2) < 0);

        var v3 = new NuGetVersion(1, 0, 0, "rc.1");
        var v4 = new NuGetVersion(1, 0, 0, "rc.2");
        Assert.True(v3.CompareTo(v4) < 0);
    }

    [Fact]
    public void CompareTo_NumericPrereleaseComponents_ComparesNumerically()
    {
        var v1 = new NuGetVersion(1, 0, 0, "rc.2");
        var v2 = new NuGetVersion(1, 0, 0, "rc.10");
        // Numeric comparison: 2 < 10
        Assert.True(v1.CompareTo(v2) < 0);
    }

    [Fact]
    public void CompareTo_MixedNumericAndAlphaPrerelease_NumericIsLess()
    {
        var numeric = new NuGetVersion(1, 0, 0, "1");
        var alpha = new NuGetVersion(1, 0, 0, "alpha");
        // Numeric components are less than alphanumeric
        Assert.True(numeric.CompareTo(alpha) < 0);
    }

    [Fact]
    public void CompareTo_DifferentPrereleaseLengths_ShorterIsLess()
    {
        var v1 = new NuGetVersion(1, 0, 0, "rc");
        var v2 = new NuGetVersion(1, 0, 0, "rc.1");
        // When all compared parts are equal, shorter is less
        Assert.True(v1.CompareTo(v2) < 0);
    }

    [Fact]
    public void CompareTo_Null_ReturnsPositive()
    {
        var v = new NuGetVersion(1, 0, 0);
        Assert.True(v.CompareTo(null) > 0);
    }

    [Fact]
    public void CompareTo_RealWorldExample_CorrectOrdering()
    {
        // From the actual test case
        var v9 = new NuGetVersion(9, 0, 100);
        var v10rc = new NuGetVersion(10, 0, 100, "rc.2.25502.107");

        Assert.True(v9.CompareTo(v10rc) < 0);
        Assert.True(v10rc.CompareTo(v9) > 0);
    }

    #endregion

    #region Equality Tests

    [Fact]
    public void Equals_SameVersions_ReturnsTrue()
    {
        var v1 = new NuGetVersion(1, 2, 3);
        var v2 = new NuGetVersion(1, 2, 3);
        Assert.True(v1.Equals(v2));
        Assert.True(v1 == v2);
        Assert.False(v1 != v2);
    }

    [Fact]
    public void Equals_SameVersionsWithPrerelease_ReturnsTrue()
    {
        var v1 = new NuGetVersion(1, 2, 3, "alpha");
        var v2 = new NuGetVersion(1, 2, 3, "alpha");
        Assert.True(v1.Equals(v2));
        Assert.True(v1 == v2);
    }

    [Fact]
    public void Equals_DifferentVersions_ReturnsFalse()
    {
        var v1 = new NuGetVersion(1, 2, 3);
        var v2 = new NuGetVersion(1, 2, 4);
        Assert.False(v1.Equals(v2));
        Assert.True(v1 != v2);
    }

    [Fact]
    public void Equals_DifferentPrerelease_ReturnsFalse()
    {
        var v1 = new NuGetVersion(1, 2, 3, "alpha");
        var v2 = new NuGetVersion(1, 2, 3, "beta");
        Assert.False(v1.Equals(v2));
    }

    [Fact]
    public void Equals_StableVsPrerelease_ReturnsFalse()
    {
        var stable = new NuGetVersion(1, 2, 3);
        var prerelease = new NuGetVersion(1, 2, 3, "alpha");
        Assert.False(stable.Equals(prerelease));
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var v = new NuGetVersion(1, 0, 0);
        Assert.False(v.Equals(null));
        Assert.False(v == null);
        Assert.True(v != null);
    }

    [Fact]
    public void Equals_SameInstance_ReturnsTrue()
    {
        var v = new NuGetVersion(1, 0, 0);
        Assert.True(v.Equals(v));
        Assert.True(v == v);
    }

    [Fact]
    public void Equals_CaseInsensitivePrerelease_ReturnsTrue()
    {
        var v1 = new NuGetVersion(1, 0, 0, "RC");
        var v2 = new NuGetVersion(1, 0, 0, "rc");
        Assert.True(v1.Equals(v2));
    }

    [Fact]
    public void GetHashCode_EqualVersions_ReturnsSameHashCode()
    {
        var v1 = new NuGetVersion(1, 2, 3, "alpha");
        var v2 = new NuGetVersion(1, 2, 3, "alpha");
        Assert.Equal(v1.GetHashCode(), v2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_CaseInsensitivePrerelease_ReturnsSameHashCode()
    {
        var v1 = new NuGetVersion(1, 0, 0, "RC");
        var v2 = new NuGetVersion(1, 0, 0, "rc");
        Assert.Equal(v1.GetHashCode(), v2.GetHashCode());
    }

    #endregion

    #region Operator Tests

    [Fact]
    public void Operators_LessThan_WorksCorrectly()
    {
        var v1 = new NuGetVersion(1, 0, 0);
        var v2 = new NuGetVersion(2, 0, 0);
        Assert.True(v1 < v2);
        Assert.False(v2 < v1);
        Assert.False(v1 < v1);
    }

    [Fact]
    public void Operators_LessThanOrEqual_WorksCorrectly()
    {
        var v1 = new NuGetVersion(1, 0, 0);
        var v2 = new NuGetVersion(2, 0, 0);
        Assert.True(v1 <= v2);
        Assert.True(v1 <= v1);
        Assert.False(v2 <= v1);
    }

    [Fact]
    public void Operators_GreaterThan_WorksCorrectly()
    {
        var v1 = new NuGetVersion(1, 0, 0);
        var v2 = new NuGetVersion(2, 0, 0);
        Assert.True(v2 > v1);
        Assert.False(v1 > v2);
        Assert.False(v1 > v1);
    }

    [Fact]
    public void Operators_GreaterThanOrEqual_WorksCorrectly()
    {
        var v1 = new NuGetVersion(1, 0, 0);
        var v2 = new NuGetVersion(2, 0, 0);
        Assert.True(v2 >= v1);
        Assert.True(v1 >= v1);
        Assert.False(v1 >= v2);
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void ToString_StableVersion_FormatsCorrectly()
    {
        var v = new NuGetVersion(1, 2, 3);
        Assert.Equal("1.2.3", v.ToString());
    }

    [Fact]
    public void ToString_PrereleaseVersion_FormatsCorrectly()
    {
        var v = new NuGetVersion(1, 2, 3, "alpha");
        Assert.Equal("1.2.3-alpha", v.ToString());
    }

    [Fact]
    public void ToString_ComplexPrereleaseVersion_FormatsCorrectly()
    {
        var v = new NuGetVersion(10, 0, 100, "rc.2.25502.107");
        Assert.Equal("10.0.100-rc.2.25502.107", v.ToString());
    }

    #endregion

    #region Sorting Tests

    [Fact]
    public void Sorting_MultipleVersions_OrdersCorrectly()
    {
        var versions = new List<NuGetVersion>
        {
            new NuGetVersion(2, 0, 0),
            new NuGetVersion(1, 0, 0, "alpha"),
            new NuGetVersion(1, 0, 0),
            new NuGetVersion(1, 0, 0, "beta"),
            new NuGetVersion(1, 1, 0),
            new NuGetVersion(1, 0, 1),
        };

        var sorted = versions.OrderBy(v => v).ToList();

        Assert.Equal(new NuGetVersion(1, 0, 0, "alpha"), sorted[0]);
        Assert.Equal(new NuGetVersion(1, 0, 0, "beta"), sorted[1]);
        Assert.Equal(new NuGetVersion(1, 0, 0), sorted[2]);
        Assert.Equal(new NuGetVersion(1, 0, 1), sorted[3]);
        Assert.Equal(new NuGetVersion(1, 1, 0), sorted[4]);
        Assert.Equal(new NuGetVersion(2, 0, 0), sorted[5]);
    }

    [Fact]
    public void Sorting_RealWorldSdkVersions_OrdersCorrectly()
    {
        var versions = new List<NuGetVersion>
        {
            new NuGetVersion(10, 0, 100, "rc.2.25502.107"),
            new NuGetVersion(9, 0, 100),
            new NuGetVersion(8, 0, 303),
            new NuGetVersion(10, 0, 100, "rc.1.25452.101"),
            new NuGetVersion(10, 0, 100),
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
        var versions = new List<NuGetVersion>
        {
            new NuGetVersion(9, 0, 100),
            new NuGetVersion(10, 0, 100, "rc.2.25502.107"),
            new NuGetVersion(8, 0, 303),
        };

        var latest = versions.OrderByDescending(v => v).First();
        Assert.Equal(new NuGetVersion(10, 0, 100, "rc.2.25502.107"), latest);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EdgeCase_ZeroVersion_WorksCorrectly()
    {
        var v = new NuGetVersion(0, 0, 0);
        Assert.Equal("0.0.0", v.ToString());
    }

    [Fact]
    public void EdgeCase_LargeVersionNumbers_WorksCorrectly()
    {
        var v = new NuGetVersion(999, 999, 999);
        Assert.Equal("999.999.999", v.ToString());
        Assert.True(NuGetVersion.TryParse("999.999.999", out _));
    }

    [Fact]
    public void EdgeCase_ComplexPrereleaseLabels_ParseCorrectly()
    {
        Assert.True(NuGetVersion.TryParse("1.0.0-alpha.1.2.3.4.5", out var v));
        Assert.Equal("alpha.1.2.3.4.5", v!.Prerelease);
    }

    [Fact]
    public void EdgeCase_PrereleaseWithNumbers_ComparesNumerically()
    {
        var versions = new List<NuGetVersion>
        {
            new NuGetVersion(1, 0, 0, "preview.2"),
            new NuGetVersion(1, 0, 0, "preview.10"),
            new NuGetVersion(1, 0, 0, "preview.1"),
        };

        var sorted = versions.OrderBy(v => v).ToList();
        Assert.Equal("1.0.0-preview.1", sorted[0].ToString());
        Assert.Equal("1.0.0-preview.2", sorted[1].ToString());
        Assert.Equal("1.0.0-preview.10", sorted[2].ToString());
    }

    #endregion
}
