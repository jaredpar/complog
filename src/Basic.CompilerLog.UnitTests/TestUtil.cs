
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Sdk;

namespace Basic.CompilerLog.UnitTests;

internal static class TestUtil
{
    internal static bool IsNetFramework => 
#if NETFRAMEWORK
        true;
#else
        false;
#endif

    internal static bool IsNetCore => !IsNetFramework;

    internal static bool InGitHubActions => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null;

    internal static string GitHubActionsTestArtifactsDirectory
    {
        get
        {
            Debug.Assert(InGitHubActions);

            var testArtifactsDir = Environment.GetEnvironmentVariable("TEST_ARTIFACTS_PATH");
            if (testArtifactsDir is null)
            {
                throw new Exception("TEST_ARTIFACTS_PATH is not set in GitHub actions");

            }

            var suffix = IsNetCore ? "netcore" : "netfx";

            return Path.Combine(testArtifactsDir, suffix);
        }
    }

    /// <summary>
    /// Internally a <see cref="IIncrementalGenerator" /> is wrapped in a type called IncrementalGeneratorWrapper. 
    /// This method will dig through that and return the original type.
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    internal static Type GetGeneratorType(object obj)
    {
        var type = obj.GetType();
        if (type.Name == "IncrementalGeneratorWrapper")
        {
            var prop = type.GetProperty(
                "Generator",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            obj = prop.GetMethod!.Invoke(obj, null)!;
        }

        return obj.GetType();
    }

    /// <summary>
    /// Run the build.cmd / .sh generated from an export command
    /// </summary>
    internal static ProcessResult RunBuildCmd(string directory) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
         ? ProcessUtil.Run("cmd", args: "/c build.cmd", workingDirectory: directory)
         : ProcessUtil.Run(Path.Combine(directory, "build.sh"), args: "", workingDirectory: directory);

    internal static string GetProjectFile(string directory) =>
        Directory.EnumerateFiles(directory, "*proj").Single();

    /// <summary>
    /// Add a project property to the project file in the current directory
    /// </summary>
    internal static void AddProjectProperty(string property, string directory)
    {
        var projectFile = GetProjectFile(directory);
        var lines = File.ReadAllLines(projectFile);
        using var writer = new StreamWriter(projectFile, append: false);
        foreach (var line in lines)
        {
            if (line.Contains("</PropertyGroup>"))
            {
                writer.WriteLine(property);
            }

            writer.WriteLine(line);
        }
    }

    internal static void SetProjectFileContent(string content, string directory)
    {
        var projectFile = GetProjectFile(directory);
        File.WriteAllText(projectFile, content);
    }
}