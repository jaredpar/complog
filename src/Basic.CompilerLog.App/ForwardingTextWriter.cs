using System.Text;

namespace Basic.CompilerLog.App;

/// <summary>
/// A TextWriter that forwards all output to an Action&lt;string&gt;
/// </summary>
internal sealed class ForwardingTextWriter(Action<string?> writeAction) : TextWriter
{
    private readonly Action<string?> WriteAction = writeAction;
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => WriteAction(value.ToString());

    public override void Write(string? value) => WriteAction(value);

    public override void WriteLine() => WriteAction(Environment.NewLine);

    public override void WriteLine(string? value)
    {
        WriteAction(value);
        WriteAction(Environment.NewLine);
    }
}
