

using System.Diagnostics;
using System.Threading.Tasks;
using Basic.CompilerLog.Util;
using BlazorMonaco.Editor;
using Microsoft.JSInterop;

namespace Basic.Blazor.Components.Pages;

public enum Mode
{
    Rsp,
    GeneratedFiles,
}

public partial class Home
{
    public CodeEditor? CodeEditor { get; set; }
    public CompilerLogReader Reader { get; private set; }
    public List<CompilerCall> CompilerCalls { get; private set; }
    public List<string> GeneratedFiles { get; private set; } = new();
    public Mode Mode { get; set; } = Mode.Rsp;
    public CompilerCall? SelectedCompilerCall { get; set; }
    public string? SelectedGeneratedFile { get; set; }

    public Home()
    {
        Reader = CompilerLogReader.Create(@"C:\Users\jaredpar\temp\console\build.complog");
        CompilerCalls = Reader.ReadAllCompilerCalls();
    }

    private async Task OnRspClicked(IJSRuntime jsRuntime, CompilerCall compilerCall)
    {
        Debug.Assert(CodeEditor is not null);
        var model = await CodeEditor.GetModel();
        await Global.SetModelLanguage(jsRuntime, model, "text");
        var arguments = compilerCall.GetArguments();
        var text = string.Join(Environment.NewLine, arguments);
        await CodeEditor.SetValue(text);
        Mode = Mode.Rsp;
    }

    private void OnGeneratedFilesClicked(CompilerCall compilerCall)
    {
        var data = Reader.ReadCompilationData(compilerCall);
        GeneratedFiles = Reader
            .ReadAllGeneratedSourceTexts(compilerCall)
            .Select(x => x.FilePath)
            .ToList();
        Mode = Mode.GeneratedFiles;
    }

    private async Task OnGeneratedFileSelected(IJSRuntime jsRuntime)
    {
        Debug.Assert(SelectedCompilerCall is not null);
        Debug.Assert(SelectedGeneratedFile is not null);
        Debug.Assert(CodeEditor is not null);

        var tuple = Reader
            .ReadAllGeneratedSourceTexts(SelectedCompilerCall)
            .FirstOrDefault(x => x.FilePath == SelectedGeneratedFile);
        string text;
        if (tuple.SourceText is null)
        {
            text = "Error: could not find file";
        }
        else
        {
            text = tuple.SourceText.ToString();
        }

        var model = await CodeEditor.GetModel();
        await Global.SetModelLanguage(jsRuntime, model, "csharp");
        await CodeEditor.SetValue(text);
    }

    private StandaloneEditorConstructionOptions CreateCodeEditorOptions(StandaloneCodeEditor editor) =>
        new()
        {
            AutomaticLayout = true,
            Language = "csharp",
            Value = """
                using System;
                Console.WriteLine("Hello, World!");
                """,
        };
}