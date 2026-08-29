
using System.Text;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public abstract class FixtureBase
{
    private int _processCount;

    protected void RunDotnetCommand(string args, string workingDirectory)
    {
        var start = DateTime.UtcNow;
        var diagnosticBuilder = new StringBuilder();

        diagnosticBuilder.AppendLine($"Running: {Interlocked.Increment(ref _processCount)} {args} in {workingDirectory}");
        var result = DotnetUtil.Command(args, workingDirectory);
        diagnosticBuilder.AppendLine($"Succeeded: {result.Succeeded}");
        diagnosticBuilder.AppendLine($"Standard Output: {result.StandardOut}");
        diagnosticBuilder.AppendLine($"Standard Error: {result.StandardError}");
        diagnosticBuilder.AppendLine($"Finished: {(DateTime.UtcNow - start).TotalSeconds:F2}s");
        TestContext.Current.SendDiagnosticMessage(diagnosticBuilder.ToString());
        if (!result.Succeeded)
        {
            Assert.Fail($"Command failed: {diagnosticBuilder.ToString()}");
        }
    }
}