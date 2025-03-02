

using System.Diagnostics;
using System.Threading.Tasks;
using Basic.CompilerLog.Util;
using BlazorMonaco.Editor;
using Microsoft.JSInterop;

namespace Basic.Blazor.Components.Pages;

public partial class Home
{
    public CodeEditor? CodeEditor { get; set; }
    public CompilerLogReader Reader { get; private set; }
    public List<CompilerCall> CompilerCalls { get; private set; }

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
        var options = await CodeEditor.GetOptions();
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