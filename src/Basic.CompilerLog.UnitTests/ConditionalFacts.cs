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
