
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Xunit;
using Xunit.Sdk;
using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using System.Collections.Immutable;


#if NET
using System.Runtime.Loader;
#endif

namespace Basic.CompilerLog.UnitTests;

internal static class TestUtil
{
    /// <summary>
    /// This is the SDK version that is used by the repository.
    /// </summary>
    internal const string SdkVersion = "10.0.100";

    /// <summary>
    /// This is the standard target framework that test projects are built against.
    /// </summary>
    internal const string TestTargetFramework = "net9.0";

    internal static bool IsNetFramework =>
#if NETFRAMEWORK
        true;
#else
        false;
#endif

    internal static bool IsNetCore => !IsNetFramework;

    internal static bool InGitHubActions => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null;

    internal static string TestArtifactsDirectory
    {
        get
        {
            if (InGitHubActions)
            {
                return TestUtil.GitHubActionsTestArtifactsDirectory;
            }

            var assemblyDir = Path.GetDirectoryName(typeof(TestBase).Assembly.Location)!;
            return Path.Combine(assemblyDir, "test-artifacts");
        }
    }

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

    internal static string TestTempRoot { get; } = CreateUniqueSubDirectory(Path.Combine(Path.GetTempPath(), "Basic.CompilerLog.UnitTests"));

    /// <summary>
    /// This code will generate a unique subdirectory under <paramref name="path"/>. This is done instead of using
    /// GUIDs because that leads to long path issues on .NET Framework.
    /// </summary>
    /// <remarks>
    /// This method is not entirely foolproof. But it does serve the purpose of creating unique directory names
    /// when tests are run in parallel on the same machine provided that we own <see cref="path"/>.
    /// </remarks>
    internal static string CreateUniqueSubDirectory(string path)
    {
        _ = Directory.CreateDirectory(path);

        var id = 0;
        while (true)
        {
            try
            {
                var filePath = Path.Combine(path, $"{id}.txt");
                var dirPath = Path.Combine(path, $"{id}");
                if (!File.Exists(filePath) && !Directory.Exists(dirPath))
                {
                    var fileStream = new FileStream(filePath, FileMode.CreateNew);
                    fileStream.Dispose();

                    _ = Directory.CreateDirectory(dirPath);
                    return dirPath;
                }
            }
            catch
            {
                // Don't care why we couldn't create the file or directory, just that it failed
            }

            id++;
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

    internal static List<string> ParseCommandLine(string commandLine)
    {
        var args = new List<string>();
        var currentArg = new StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (currentArg.Length > 0)
                {
                    args.Add(currentArg.ToString());
                    currentArg.Clear();
                }

                continue;
            }

            currentArg.Append(c);
        }

        if (currentArg.Length > 0)
        {
            args.Add(currentArg.ToString());
        }

        return args;
    }
}
