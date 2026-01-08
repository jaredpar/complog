using System.Drawing;
using System.Reflection;
using Basic.CompilerLog.Util;

namespace Basic.CompilerLog.App;

internal class ServerUtil
{
    internal TextWriter Output { get; }
    internal CustomCompilerLoadContext LoadContext { get; }
    internal Assembly Assembly { get; }

    internal ServerUtil(TextWriter output, CustomCompilerLoadContext loadContext)
    {
        Output = output;
        LoadContext = loadContext;
        Assembly = loadContext.LoadFromAssemblyName(new("csc"));
    }

    internal void RunCompilations(List<(string RspFilePath, string Name, CompilerCall CompilerCall)> compilerCalls, string clientDirectory, string tempDir, int maxParallel)
    {
        var pipeName = Guid.NewGuid().ToString("N");
        var logger = new EmptyCompilerServerLoggerAccessor(Assembly).Instance;
        var connection = new BuildServerConnectionAccessor(Assembly);
        var tasks = new List<Task>(capacity: maxParallel);
        var index = 0;
        var completed = 0;

        try
        {
            do
            {
                while (tasks.Count < maxParallel && index < compilerCalls.Count)
                {
                    var tuple = compilerCalls[index];
                    Output.WriteLine($"Queueing {tuple.Name}");
                    var buildRequest = connection.CreateBuildRequest(
                        tuple.Name,
                        tuple.CompilerCall.IsCSharp
                            ? 0x44532521
                            : 0x44532522,
                        File.ReadAllLines(tuple.RspFilePath).ToList(),
                        workingDirectory: Path.GetDirectoryName(tuple.RspFilePath)!,
                        tempDirectory: tempDir,
                        keepAlive: null,
                        libDirectory: null,
                        compilerHash: null);
                    var task = connection.RunServerBuildRequestAsync(
                        buildRequest!,
                        pipeName,
                        clientDirectory,
                        logger,
                        CancellationToken.None);
                    tasks.Add((Task)task!);
                    index++;
                }

                var completedTask = Task.WhenAny(tasks).Result;
                // Get the Result property via reflection
                var resultProperty = completedTask.GetType().GetProperty("Result")!;
                var result = resultProperty.GetValue(completedTask);
                Output.WriteLine($"Completed {compilerCalls[completed].Name} with result: {result}");
                tasks.Remove(completedTask);
                completed++;
            } while (completed < compilerCalls.Count);
        }
        finally
        {
            connection.RunServerShutdownRequestAsync(
                pipeName,
                timeoutOverride: null,
                waitForProcess: true,
                logger,
                CancellationToken.None);
        }
    }
}

/// <summary>
/// Provides reflection-based access to the BuildServerConnection type from Microsoft.CodeAnalysis
/// </summary>
internal sealed class BuildServerConnectionAccessor
{
    private readonly Type _buildServerConnectionType;
    private readonly MethodInfo _createBuildRequestMethod;
    private readonly MethodInfo _runServerShutdownRequestAsyncMethod;
    private readonly MethodInfo _runServerBuildRequestAsyncParamsMethod;

    internal BuildServerConnectionAccessor(Assembly assembly)
    {
        _buildServerConnectionType = assembly.GetType("Microsoft.CodeAnalysis.CommandLine.BuildServerConnection", throwOnError: true)!;

        // Find CreateBuildRequest method
        _createBuildRequestMethod = _buildServerConnectionType.GetMethod(
            "CreateBuildRequest",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(string),           // requestId
                assembly.GetType("Microsoft.CodeAnalysis.CommandLine.RequestLanguage", throwOnError: true)!, // language
                typeof(List<string>),     // arguments
                typeof(string),           // workingDirectory
                typeof(string),           // tempDirectory
                typeof(string),           // keepAlive
                typeof(string),           // libDirectory
                typeof(string)            // compilerHash
            ],
            modifiers: null)!;

        // Find RunServerShutdownRequestAsync method
        _runServerShutdownRequestAsyncMethod = _buildServerConnectionType.GetMethod(
            "RunServerShutdownRequestAsync",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(string),                // pipeName
                typeof(int?),                  // timeoutOverride
                typeof(bool),                  // waitForProcess
                assembly.GetType("Microsoft.CodeAnalysis.CommandLine.ICompilerServerLogger", throwOnError: true)!, // logger
                typeof(CancellationToken)      // cancellationToken
            ],
            modifiers: null)!;

        // Find RunServerBuildRequestAsync (2 params version - the simple overload)
        var buildRequestAsyncMethods = _buildServerConnectionType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(m => m.Name == "RunServerBuildRequestAsync")
            .ToArray();

        _runServerBuildRequestAsyncParamsMethod = buildRequestAsyncMethods
            .First(m => m.GetParameters().Length == 5); // buildRequest, pipeName, clientDirectory, logger, cancellationToken
    }

    /// <summary>
    /// Creates a BuildRequest object using reflection
    /// </summary>
    public object? CreateBuildRequest(
        string requestId,
        int language, // RequestLanguage enum value (0 = CSharpCompile, 1 = VisualBasicCompile)
        List<string> arguments,
        string workingDirectory,
        string? tempDirectory,
        string? keepAlive,
        string? libDirectory,
        string? compilerHash)
    {
        var languageEnum = Enum.ToObject(
            _createBuildRequestMethod.GetParameters()[1].ParameterType,
            language);

        return _createBuildRequestMethod.Invoke(
            null,
            [
                requestId,
                languageEnum,
                arguments,
                workingDirectory,
                tempDirectory,
                keepAlive,
                libDirectory,
                compilerHash
            ]);
    }

    /// <summary>
    /// Runs a server shutdown request using reflection
    /// </summary>
    public object? RunServerShutdownRequestAsync(
        string pipeName,
        int? timeoutOverride,
        bool waitForProcess,
        object logger, // ICompilerServerLogger
        CancellationToken cancellationToken) =>
        _runServerShutdownRequestAsyncMethod.Invoke(
            null,
            [
                pipeName,
                timeoutOverride,
                waitForProcess,
                logger,
                cancellationToken
            ]);

    /// <summary>
    /// Runs a server build request using reflection (simple overload with 5 parameters)
    /// </summary>
    public object? RunServerBuildRequestAsync(
        object buildRequest,
        string pipeName,
        string clientDirectory,
        object logger, // ICompilerServerLogger
        CancellationToken cancellationToken) =>
        _runServerBuildRequestAsyncParamsMethod.Invoke(
            null,
            [
                buildRequest,
                pipeName,
                clientDirectory,
                logger,
                cancellationToken
            ]);

    private static async Task<object> WrapTaskResult(object task)
    {
        // Use reflection to await the Task<BuildResponse> and return as object
        var taskType = task.GetType();
        var resultProperty = taskType.GetProperty("Result")!;

        // Await the task
        await (dynamic)task;

        // Get the result
        return resultProperty.GetValue(task)!;
    }
}

/// <summary>
/// Provides reflection-based access to the EmptyCompilerServerLogger type from Microsoft.CodeAnalysis
/// </summary>
internal sealed class EmptyCompilerServerLoggerAccessor
{
    private readonly Type _emptyCompilerServerLoggerType;
    private readonly PropertyInfo _instanceProperty;

    internal EmptyCompilerServerLoggerAccessor(Assembly assembly)
    {
        _emptyCompilerServerLoggerType = assembly.GetType("Microsoft.CodeAnalysis.CommandLine.EmptyCompilerServerLogger", throwOnError: true)!;

        // Find the Instance static property
        _instanceProperty = _emptyCompilerServerLoggerType.GetProperty(
            "Instance",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    }

    /// <summary>
    /// Gets the singleton Instance of EmptyCompilerServerLogger
    /// </summary>
    public object Instance => _instanceProperty.GetValue(null)!;
}
