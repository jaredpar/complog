using System.Buffers;
using System.Diagnostics;
using System.Drawing.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

namespace Scratch;

internal sealed class StallDetector
{
    internal sealed class ProjectInstance(string projectFilePath, int projectInstanceId, int nodeId, string key)
    {
        internal string ProjcetFilePath { get; } = projectFilePath;
        internal string ProjectFileName { get; } = Path.GetFileName(projectFilePath);
        internal int ProjectId { get; } = projectInstanceId;
        internal int NodeId { get; } = nodeId;
        internal string Key {  get ; } = key;
        public override string ToString() => $"{ProjectFileName} {key}";
    };

    internal sealed class ProjectContext(ProjectInstance projectInstance, string? targetNames, int? parentContextId, int evaluationId)
    {
        internal ProjectInstance ProjectInstance { get; } = projectInstance;
        internal string? TargetNames { get; } = targetNames;
        internal int? ParentContextId { get; } = parentContextId;
        internal ProjectContext? Parent { get; set; }
        internal string ProjectFileName => ProjectInstance.ProjectFileName;
        internal int EvaluationId { get; } = evaluationId;
        public override string ToString() => $"{ProjectInstance.ProjectFileName} Targets: {TargetNames}";
    }

    public static void Go(string binlogFilePath)
    {
        using var stream = File.OpenRead(binlogFilePath);
        var records = BinaryLog.ReadRecords(stream);
        var instanceMap = new Dictionary<int, ProjectInstance>();
        var contextMap = new Dictionary<int, ProjectContext>();

        foreach (var record in records)
        {
            // It's important to filter out invalid EvaluationIds here. Unclear why but they can re-use
            // project instance ids. In that combination they will show up as having occurred in different
            // node ids. Trying to understand this
            if (record.Args is ProjectStartedEventArgs { BuildEventContext: { } context } e)
            {
                if (GetProjectInstanceId(context) is not int projectInstanceId)
                {
                    continue;
                }

                var key = GetKey(e.GlobalProperties);
                if (instanceMap.TryGetValue(projectInstanceId, out var projectInstance))
                {
                    Assert(projectInstance.NodeId == context.NodeId, "Project instance started on different nodes");
                }
                else
                {
                    projectInstance = new ProjectInstance(e.ProjectFile ?? "<unknown>", projectInstanceId, context.NodeId, key);
                    instanceMap[projectInstanceId] = projectInstance;
                }

                int? parentContextId = null;
                if (e.ParentProjectBuildEventContext is { ProjectContextId: not BuildEventContext.InvalidProjectContextId })
                {
                    parentContextId = e.ParentProjectBuildEventContext.ProjectContextId;
                }

                Assert(!contextMap.ContainsKey(context.ProjectContextId), "Project context already exists");
                contextMap[context.ProjectContextId] = new ProjectContext(projectInstance, e.TargetNames, parentContextId, context.EvaluationId);
            }
        }

        foreach (var context in contextMap.Values)
        {
            if (context.ParentContextId is { } parentContextId)
            {
                if (!contextMap.TryGetValue(parentContextId, out var parentContext))
                {
                    Assert(false, "Cannot find parent context");
                    continue;
                }
                context.Parent = parentContext;
            }
        }

        foreach (var context in contextMap.Values.Where(c => c.Parent is null))
        {
            PrintContext(context, 0);
        }

        void PrintContext(ProjectContext context, int indentLength)
        {
            var indent = new string(' ', indentLength);
            Console.WriteLine($"{indent}Project: {context.ProjectFileName} Target Names: {context.TargetNames}");
            foreach (var c in contextMap.Values.Where(c => c.Parent == context))
            {
                PrintContext(c, indentLength + 2);
            }
        }
    }

    /// <summary>
    /// Get the project instance ID from the build event context. This handles the case where the build
    /// log recorded invalid evaluation or project instance ids. Those bugs will be significantly by this
    /// PR
    ///
    /// https://github.com/dotnet/msbuild/pull/12946
    /// </summary>
    private static int? GetProjectInstanceId(BuildEventContext context) =>
        (context.EvaluationId, context.ProjectInstanceId) switch
        {
            (BuildEventContext.InvalidEvaluationId, BuildEventContext.InvalidProjectInstanceId) => null,
            (BuildEventContext.InvalidEvaluationId, var id) => id,
            (var id, BuildEventContext.InvalidProjectInstanceId) => id,
            (_, var id) => id,
        };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            Console.WriteLine("WARNING: " + message);
        }
    }

    private static string GetKey(IDictionary<string, string>? properties)
    {
        if (properties is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var kvp in properties.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            sb.Append($"{kvp.Key}={kvp.Value};");
        }

        // return a sha256 of the string to keep it short
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}

