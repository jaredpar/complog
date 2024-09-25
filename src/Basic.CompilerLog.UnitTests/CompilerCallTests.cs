
using Basic.CompilerLog.Util;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public class CompilerCallTests
{
    [Fact]
    public void GetDiagnosticNameNoTargetFramework()
    {
        var compilerCall = new CompilerCall(
            compilerFilePath: null,
            "test.csproj",
            CompilerCallKind.Regular,
            targetFramework: null,
            isCSharp: true,
            arguments: new (() => []));
        Assert.Null(compilerCall.TargetFramework);
        Assert.Equal(compilerCall.ProjectFileName, compilerCall.GetDiagnosticName());
    }
}