namespace Basic.CompilerLog.UnitTests;

using System.Runtime.InteropServices;
using Xunit;

public sealed class WindowsFactAttribute : FactAttribute
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public WindowsFactAttribute()
    {
        SkipUnless = nameof(WindowsFactAttribute.IsWindows);
        SkipType = typeof(WindowsFactAttribute);
        Skip = "This test is only supported on Windows";
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        SkipUnless = nameof(WindowsFactAttribute.IsWindows);
        SkipType = typeof(WindowsFactAttribute);
        Skip = "This test is only supported on Windows";
    }
}

public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public static bool IsUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public UnixTheoryAttribute()
    {
        Skip = "This test is only supported on Unix";
        SkipUnless = nameof(IsUnix);
        SkipType = typeof(UnixTheoryAttribute);
    }
}

