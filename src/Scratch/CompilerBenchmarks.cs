using Basic.CompilerLog;
using Basic.CompilerLog.Util;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable

namespace Scratch;

[MemoryDiagnoser]
public class CompilerBenchmark
{
    public string TempDirectory { get; set; }
    public string CompilerLogPath { get; set; }

    [GlobalSetup]
    public void GenerateLog()
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), nameof(CompilerBenchmark), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDirectory);
        DotnetUtil.Command($"new console --name example --output .", TempDirectory);
        DotnetUtil.Command($"build -bl", TempDirectory);
        CompilerLogPath = Path.Combine(TempDirectory, "build.complog");
        CompilerLogUtil.ConvertBinaryLog(Path.Combine(TempDirectory, "msbuild.binlog"), CompilerLogPath);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Directory.Delete(TempDirectory, recursive: true);
    }

    [ParamsAllValues]
    public BasicAnalyzerKind Kind { get; set; }

    /*
    [Benchmark]
    public void Emit()
    {
        var data = Reader.ReadCompilationData(0, Options);
        var compilation = data.GetCompilationAfterGenerators();
        var stream = new MemoryStream();
        var result = compilation.Emit(
            stream,
            options: data.EmitOptions);
        if (!result.Success)
        {
            throw new Exception("compilation failed");
        }
        data.BasicAnalyzers.Dispose();
    }
    */

    [Benchmark]
    public void LoadAnalyzers()
    {
        using var reader = CompilerLogReader.Create(CompilerLogPath, Kind);
        var compilerCall = reader.ReadCompilerCall(0);
        var analyzers = reader.CreateBasicAnalyzerHost(compilerCall);
        foreach (var analyzer in analyzers.AnalyzerReferences)
        {
            _ = analyzer.GetAnalyzersForAllLanguages();
            _ = analyzer.GetGeneratorsForAllLanguages();
        }
        analyzers.Dispose();
    }
}
