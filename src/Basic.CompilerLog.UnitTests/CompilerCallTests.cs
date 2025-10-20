
using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public class CompilerCallTests
{
    [Fact]
    public void GetDiagnosticNameNoTargetFramework()
    {
        var compilerCall = new CompilerCall("test.csproj", CompilerCallKind.Regular, targetFramework: null, isCSharp: true, compilerFilePath: null);
        Assert.Null(compilerCall.TargetFramework);
        Assert.Equal(compilerCall.ProjectFileName, compilerCall.GetDiagnosticName());
    }
}
