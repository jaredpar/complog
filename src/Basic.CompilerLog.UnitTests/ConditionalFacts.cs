namespace Basic.CompilerLog.UnitTests;

using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test is only supported on Windows";
        }
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test is only supported on Windows";
        }
    }
}

public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test is only supported on Windows";
        }
    }
}

