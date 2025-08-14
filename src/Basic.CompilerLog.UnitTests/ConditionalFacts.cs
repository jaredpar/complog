namespace Basic.CompilerLog.UnitTests;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

public sealed class WindowsFactAttribute : FactAttribute
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
      : base(sourceFilePath, sourceLineNumber)
    {
        SkipUnless = nameof(WindowsFactAttribute.IsWindows);
        SkipType = typeof(WindowsFactAttribute);
        Skip = "This test is only supported on Windows";
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
      : base(sourceFilePath, sourceLineNumber)
    {
        SkipUnless = nameof(WindowsFactAttribute.IsWindows);
        SkipType = typeof(WindowsFactAttribute);
        Skip = "This test is only supported on Windows";
    }
}

public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public static bool IsUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public UnixTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
      : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "This test is only supported on Unix";
        SkipUnless = nameof(IsUnix);
        SkipType = typeof(UnixTheoryAttribute);
    }
}

