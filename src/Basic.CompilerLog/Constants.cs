using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

internal static class Constants
{
    internal const int ExitFailure = 1;
    internal const int ExitSuccess = 0;

    internal static string CurrentDirectory { get; set; } = Environment.CurrentDirectory;
}
