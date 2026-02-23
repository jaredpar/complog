using System;

namespace Basic.CompilerLog.Util;

[Flags]
public enum ExportOptions
{
    None = 0,
    ExcludeAnalyzers = 1 << 0,
    ExcludeConfigs = 1 << 1,
    ExcludeAll = ExcludeAnalyzers | ExcludeConfigs,
}
