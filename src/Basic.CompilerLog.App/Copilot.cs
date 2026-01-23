
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Security.Permissions;
using Basic.CompilerLog.Util;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace Basic.CompilerLog.App;

internal sealed class CopilotUtil()
{
    private const string CorePrompt = """
        You are an AI assistant that helps users explore and diagonose compiler issues that
        happen as part of building their projects. You have access to tools that allow you to see the projects
        being built, the arguments passed to the compiler, look at the content of any input and the diagnostics
        produced by the compiler.
        """;

    private const string DescriptionProjectId = "The ID of the project to use in the tool";

    internal async Task Run(ICompilerCallReader reader, TextWriter output)
    {
        await using var client = new CopilotClient();
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = "gpt-4.1",
            Streaming = true,
            Tools = CreateTools(reader),
        });

        // Listen for response chunks
        session.On(ev =>
        {
            if (ev is AssistantMessageDeltaEvent deltaEvent)
            {
                Console.Write(deltaEvent.Data.DeltaContent);
            }
            if (ev is SessionIdleEvent)
            {
                Console.WriteLine();
            }
        });

        await session.SendAsync(new MessageOptions() { Prompt = CorePrompt });
        Console.WriteLine("Welcome to the Compiler Log Copilot!");

        while (true)
        {
            var userInput = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(userInput) || userInput.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await session.SendAsync(new MessageOptions() { Prompt = userInput });
        }
    }

    internal static List<AIFunction> CreateTools(ICompilerCallReader reader)
    {
        var compilerCalls = reader.ReadAllCompilerCalls(x => x.Kind == CompilerCallKind.Regular);
        var compilerCallMap = compilerCalls.ToDictionary(x => x.GetDiagnosticName(), x => x);
        var compilationDataMap = new Dictionary<string, CompilationData>();

        var list = new List<AIFunction>();

        list.Add(AIFunctionFactory.Create(
            () => compilerCalls.Select(x => new
                {
                    ProjectId = x.GetDiagnosticName(),
                    ProjectFilePath = x.ProjectFilePath,
                    TargetFramework = x.TargetFramework,
                }),
            "get_compiler_calls",
            "Get all of the compiler calls in the log."));

        list.Add(AIFunctionFactory.Create(
            ([Description(DescriptionProjectId)] string projectId) =>
            {
                if (!compilerCallMap.TryGetValue(projectId, out var compilerCall))
                {
                    throw new ArgumentException($"No compiler call found with ProjectId '{projectId}'.");
                }

                return reader.ReadArguments(compilerCall);
            },
            "get_compiler_call_arguments",
            "Get all of the inputs to a specific compiler call"));

        list.Add(AIFunctionFactory.Create(
            async ([Description(DescriptionProjectId)] string projectId) =>
            {
                if (!compilationDataMap.TryGetValue(projectId, out var compilationData))
                {
                    if (!compilerCallMap.TryGetValue(projectId, out var compilerCall))
                    {
                        throw new ArgumentException($"No compiler call found with ProjectId '{projectId}'.");
                    }

                    compilationData = reader.ReadCompilationData(compilerCall);
                    compilationDataMap[projectId] = compilationData;
                }

                var diagnostics = await compilationData.GetAllDiagnosticsAsync();
                return diagnostics.Select(x => x.GetMessage());
            },
            "get_compiler_diagnostics",
            "Get all of the diagnostics produced by a specific compiler call"));

        return list;
    }
}
