using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Basic.CompilerLog.Util;

public static partial class Constants
{
    public const int ExitFailure = 1;
    public const int ExitSuccess = 0;

    public static string CurrentDirectory { get; set; } = Environment.CurrentDirectory;
    public static string LocalAppDataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Basic.CompilerLog");
    public static TextWriter Out { get; set; } = Console.Out;

    public static Action<ICompilerCallReader> OnCompilerCallReader = _ => { };
}
