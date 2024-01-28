
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class PathUtilTests
{
    [Fact]
    public void RemovePathStart()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Core(@"C:\a\b\c.cs", @"C:\a\", @"b\c.cs");
            Core(@"a\b\c.cs", @"a\", @"b\c.cs");
        }
        else
        {
            Core("a/b/c.cs", "a/", "b/c.cs");
            Core("a/b/c.cs", "a", "b/c.cs");
        }

        static void Core(string filePath, string start, string expected)
        {
            Assert.Equal(expected, PathUtil.RemovePathStart(filePath, start));
        }
    }
}