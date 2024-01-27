using Microsoft.Build.Logging.StructuredLogger;

namespace Basic.CompilerLog.Util;

public sealed class CompilerLogException(string message) : Exception(message);
