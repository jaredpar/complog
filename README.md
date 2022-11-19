# Compiler Logs

This is the repository for creating and consuming compiler log files. These are files created from a [MSBuild binary log](https://github.com/KirillOsenkov/MSBuildStructuredLog) that contain information necessary to recreate all of the [Compilation](https://docs.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation?view=roslyn-dotnet-4.2.0) instances from that build. 

The compiler log files are self contained. They must be created on the same machine where the binary log was created but after creation they can be freely copied between machines. That enables a number of scenarios:

1. GitHub pipelines can cleanly separate build and build analysis into different legs. The analysis can be done on a separate machine entirely independent of where the build happens.
1. Allows for easier customer investigations by the C# / VB compiler teams. Instead of trying to re-create a customer build environment, customers can provide a compiler log file that developers can easily open with a call to the API.

## complog

This global tool can be installed via 

> dotnet tool install --global Basic.CompilerLog

From there the following commands are available:

- `create`: create a compilerlog file from an existing binary log
- `diagnostics`: print diagnostics from the specified compilations
- `export`: export complete compilations to disk
- `ref`: export references for a compilation to disk
- `rsp`: generate rsp files for compilation events
- `print`: print the summary of a compilerlog on the command line

## Info

:warning: A compiler log will include potentially sensitive artifacts :warning:

A compiler log file contains all of the information necessary to recreate a `Compilation`. That includes all source, resources, references, strong name keys, etc .... That will be visible to anyone you provide a compiler log to.
