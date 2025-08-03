#if NET

using System.Reflection;
using Basic.CompilerLog.App;
using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public class ProgramTests
{
    [Fact]
    public void TestConsistentVersions()
    {
        AssertConsistentVersions(typeof(ProgramHolder).Assembly);
        AssertConsistentVersions(typeof(CompLogApp).Assembly);
        AssertConsistentVersions(typeof(CompilerLogReader).Assembly);
        static void AssertConsistentVersions(Assembly assembly)
        {
            var references = assembly
                .GetReferencedAssemblies()
                .Where(a => a.Name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.NotEmpty(references);

            var version = references[0].Version;
            Assert.All(references, r => Assert.Equal(version, r.Version));
        }
    }

    [Fact]
    public void TestApplicationHasHigherVersion()
    {
        var appVersion = GetRoslynVersion(typeof(ProgramHolder).Assembly);
        var libVersion = GetRoslynVersion(typeof(CompLogApp).Assembly);
        Assert.True(appVersion > libVersion);

        Version GetRoslynVersion(Assembly assembly) => assembly
            .GetReferencedAssemblies()
            .Single(x => x.Name == "Microsoft.CodeAnalysis")
            .Version!;
    }
}

#endif