
using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public class CompilerCallTests
{
    [Fact]
    public void GetDiagnosticNameNoTargetFramework()
    {
        var compilerCall = new CompilerCall("test.csproj");
        Assert.Null(compilerCall.TargetFramework);
        Assert.Equal(compilerCall.ProjectFileName, compilerCall.GetDiagnosticName());
        Assert.Empty(compilerCall.GetArguments());
    }
}