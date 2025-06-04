using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Basic.CompilerLog.Util;
using Octokit;

namespace Basic.CompilerLog.GitHub;

public sealed class GitHubUtil(GitHubClient client)
{
    public GitHubClient Client { get; } = client;

    public async Task<CompilerLogReader> GetLatestCompilerLogStream(string owner, string repo)
    {
        await foreach (var artifact in GetLatestCompilerLogArtifacts(owner, repo).ConfigureAwait(false))
        {
            var stream = await Client.Actions.Artifacts.DownloadArtifact(owner, repo, artifact.Id, "zip");
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.EndsWith(".complog"))
                {
                    var entryStream = entry.Open();
                    return CompilerLogReader.Create(entryStream, state: null, leaveOpen: true);
                }
            }
        }

        throw new Exception("Could not find log");
    }

    public async IAsyncEnumerable<Artifact> GetLatestCompilerLogArtifacts(string owner, string repo)
    {
        await foreach (var workflow in GetBuildWorkFlows(owner, repo).ConfigureAwait(false))
        {
            var runId = await GetLatestRunId(workflow).ConfigureAwait(false);
            if (runId is null)
            {
                continue;
            }

            var artifacts = await Client.Actions.Artifacts.ListWorkflowArtifacts(owner, repo, runId.Value).ConfigureAwait(false);
            foreach (var artifact in artifacts.Artifacts)
            {
                if (artifact.Name.EndsWith(".complog"))
                {
                    yield return artifact;
                }
            }
        }

        async Task<long?> GetLatestRunId(Workflow workflow)
        {
            var request = new WorkflowRunsRequest()
            {
                Status = CheckRunStatusFilter.Success,
                ExcludePullRequests = true,
            };

            var options = new ApiOptions()
            {
                PageSize = 10,
                PageCount = 1,
                StartPage = 1,
            };

            var runs = await Client.Actions.Workflows.Runs.List(owner, repo, request, options);
            return runs.WorkflowRuns
                .OrderByDescending(x => x.RunNumber)
                .Where(x => x.HeadBranch == "main" || x.HeadBranch == "master")
                .Select(x => x.Id)
                .FirstOrDefault();
        }
    }

    public async IAsyncEnumerable<Workflow> GetBuildWorkFlows(string owner, string repo)
    {
        var response = await Client.Actions.Workflows.List(owner, repo).ConfigureAwait(false);
        foreach (var workflow in response.Workflows)
        {
            if (workflow.Name.Contains("build", StringComparison.OrdinalIgnoreCase))
            {
                yield return workflow;
            }
        }
    }

    public static GitHubUtil CreateFromGitHubCli()
    {
        string GetGhToken()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth status --show-token --hostname github.com",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || output is null)
                throw new Exception("Failed to get token from `gh auth`. Is GitHub CLI installed and logged in?");

            using var reader = new StringReader(output);
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("Token: "))
                {
                    return line.Split("Token: ")[1].Trim();
                }
            }

            throw new Exception($"Could not parse token from output {output}");
        }

        var token = GetGhToken();
        var client = new GitHubClient(new ProductHeaderValue("CompilerLogCli"))
        {
            Credentials = new Credentials(token)
        };

        return new(client);
    }
}


