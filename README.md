Compiler Logs
===

This is the repository for creating and consuming compiler log files. These are files created from a [MSBuild binary log](https://github.com/KirillOsenkov/MSBuildStructuredLog) that contain information necessary to recreate all of the [Compilation](https://docs.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.compilation?view=roslyn-dotnet-4.2.0) instances from that build. 

The compiler log files are self contained. They must be created on the same machine where the binary log was created but after creation they can be freely copied between machines. That enables a number of scenarios:

1. GitHub pipelines can cleanly separate build and build analysis into different legs. The analysis can be done on a separate machine entirely independent of where the build happens.
1. Allows for easier customer investigations by the C# / VB compiler teams. Instead of trying to re-create a customer build environment, customers can provide a compiler log file that developers can easily open with a call to the API.

## complog

This global tool can be installed via 

> dotnet tool install --global complog

From there the following commands are available:

- `create`: create a complog file from an existing binary log
- `replay`: replay the builds from the complog
- `export`: export complete compilations to disk
- `ref`: export references for a compilation to disk
- `rsp`: generate rsp files for compilation events
- `print`: print the summary of a complog on the command line

## Info

:warning: A compiler log **will** include potentially sensitive artifacts :warning:

A compiler log file contains all of the information necessary to recreate a `Compilation`. That includes all source, resources, references, strong name keys, etc .... That will be visible to anyone you provide a compiler log to.

## Creating Compiler Logs
There are a number of ways to create a compiler log. The easiest is to create it off of a [binary log](https://github.com/dotnet/msbuild/blob/main/documentation/wiki/Binary-Log.md) file from a previous build.

```cmd
> msbuild -bl MySolution.sln
> complog create msbuild.binlog
```

By default this will include every project in the binary log. If there are a lot of projects this can produce a large compiler log. You can use the `-p` option to limit the compiler log to a specific set of projects.

```cmd
> complog create msbuild.binlog -p MyProject.csproj
```

For solutions or projects that can be built with `dotnet build` a compiler log can be created by just running `create` against the solution or project file directly.

```cmd
> complog create MyProject.csproj
```

When trying to get a compiler log from a build that occurs in a GitHub action you can use the `complog-action` action to simplify creating and uploading the compiler log.

```yml
  - name: Build .NET app
    run: dotnet build -bl

  - name: Create and upload the compiler log
    uses: jaredpar/compilerlog-action@v1
    with:
      binlog: msbuild.binlog
```

## Debugging Compiler Logs
### Running locally
To re-run all of the compilations in a compiler log use the `replay` command

```cmd
> complog replay build.complog
Microsoft.VisualStudio.IntegrationTest.IntegrationService.csproj (net472) ...Success
Roslyn.Test.Performance.Utilities.csproj (net472) ...Success
Microsoft.CodeAnalysis.XunitHook.csproj (net472) ...Success
```

Passing the `-export` argument will cause all failed compilations to be exported to the local disk for easy analysis.

### Debugging in Visual Studio
To debug a compilation in Visual Studio first export it to disk: 

```cmd
> complog export build.complog
```

That will write out all the artifacts necessary to run a command line build to disk. Use the `--project` option to limit the output to specific projects. For each project it will generate a build.rsp command that uses the exported arguments. It will also generate several `build*.cmd` files. Those will execute `dotnet exec csc.dll @build.rsp` on the build for every SDK installed on the machine. 

![example of export output](/docs/images/debug-rsp-1.png)

The next step is to setup csc / vbc to use the build.rsp file for debugging. Open the debug settings for csc / vbc and set them to have the argument `@build.rsp` and make the working directory the location of that file.

![example of debug settnigs](/docs/images/debug-rsp-2.png)

Then launch csc / vbc and it will debug that project.




