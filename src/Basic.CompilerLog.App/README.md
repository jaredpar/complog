# Basic.CompilerLog.Core

This is functionally the `complog` application.

This exists as a separate project so that it can be compiled against an older Roslyn API while the actual complog application can use the latest Roslyn API. Doing this means that when a custom compiler is specified on the command line, there is a much higher chance that it will succeed. Without this separation, if the custom compiler was an older one then it's possible `complog` will fail because it used an API that is not available in the older compiler.

The use of older compilers is important because it happens in regression testing. For example when we're trying to run bisect or narrow down the SDK where a particular regression was introduced.
