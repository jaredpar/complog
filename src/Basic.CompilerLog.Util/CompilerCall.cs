namespace Basic.CompilerLog.Util;

public enum CompilerCallKind
{
    Regular,
    Satellite
}

public sealed class CompilerCall
{
    public string ProjectFilePath { get; }
    public CompilerCallKind Kind { get; }
    public string? TargetFramework { get; }
    public bool IsCSharp { get; }
    public string[] Arguments { get; }

    public bool IsVisualBasic => !IsCSharp;
    public string ProjectFileName => Path.GetFileName(ProjectFilePath);

    internal CompilerCall(string projectFilePath, CompilerCallKind kind, string? targetFramework, bool isCSharp, string[] arguments)
    {
        ProjectFilePath = projectFilePath;
        Kind = kind;
        TargetFramework = targetFramework;
        IsCSharp = isCSharp;
        Arguments = arguments;
    }
}
