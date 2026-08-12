using System.Collections;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class DotnetUtilTests
{
    [Fact]
    public void CreateDotnetEnvironmentVariables_ParentSdkPaths_Removed()
    {
        var environmentVariables = new Hashtable
        {
            ["MSBuildExtensionsPath"] = "parent-extensions",
            ["msbuildsdkspath"] = "parent-sdks",
            ["MSBuildExtensionsPath32"] = "machine-extensions",
            ["MSBuildExtensionsPath64"] = "machine-extensions-64",
            ["MSBuildLoadMicrosoftTargetsReadOnly"] = "true",
            ["DOTNET_HOST_PATH"] = "dotnet",
        };

        var childEnvironment = DotnetUtil.CreateDotnetEnvironmentVariables(environmentVariables);

        Assert.False(childEnvironment.ContainsKey("MSBuildExtensionsPath"));
        Assert.False(childEnvironment.ContainsKey("msbuildsdkspath"));
        Assert.Equal(4, childEnvironment.Count);
        Assert.Equal("machine-extensions", childEnvironment["MSBuildExtensionsPath32"]);
        Assert.Equal("machine-extensions-64", childEnvironment["MSBuildExtensionsPath64"]);
        Assert.Equal("true", childEnvironment["MSBuildLoadMicrosoftTargetsReadOnly"]);
        Assert.Equal("dotnet", childEnvironment["DOTNET_HOST_PATH"]);
    }

    [UnixFact]
    public void CreateDotnetEnvironmentVariables_CaseDistinctVariables_Preserved()
    {
        var environmentVariables = new Hashtable
        {
            ["Path"] = "mixed-case",
            ["PATH"] = "upper-case",
        };

        var childEnvironment = DotnetUtil.CreateDotnetEnvironmentVariables(environmentVariables);

        Assert.Equal(2, childEnvironment.Count);
        Assert.Equal("mixed-case", childEnvironment["Path"]);
        Assert.Equal("upper-case", childEnvironment["PATH"]);
    }
}
