using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class UnitTestsTests
{
    [Fact]
    public void CreateUniqueSubDirectory()
    {
        var root = new TempDir();
        var path1 = TestUtil.CreateUniqueSubDirectory(root.DirectoryPath);
        Assert.True(Directory.Exists(path1));
        var path2 = TestUtil.CreateUniqueSubDirectory(root.DirectoryPath);
        Assert.True(Directory.Exists(path2));
        Assert.NotEqual(path1, path2);
    }
}
