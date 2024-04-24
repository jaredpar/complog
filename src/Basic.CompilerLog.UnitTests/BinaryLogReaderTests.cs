using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Impl;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.InteropServices;
#if NETCOREAPP
using System.Runtime.Loader;
#endif
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

[Collection(CompilerLogCollection.Name)]
public sealed class BinaryLogReaderTests : TestBase
{
    public CompilerLogFixture Fixture { get; }

    public BinaryLogReaderTests(ITestOutputHelper testOutputHelper, CompilerLogFixture fixture)
        : base(testOutputHelper, nameof(CompilerLogReaderTests))
    {
        Fixture = fixture;
    }

    [Fact]
    public void ReadCommandLineArgumentsOwnership()
    {
        using var reader = BinaryLogReader.Create(Fixture.Console.Value.BinaryLogPath!);
        var compilerCall = reader.ReadAllCompilerCalls().First();
        Assert.NotNull(reader.ReadCommandLineArguments(compilerCall));

        var args = compilerCall.GetArguments();
        compilerCall = new CompilerCall(
            compilerCall.CompilerFilePath,
            compilerCall.ProjectFilePath,
            compilerCall.Kind,
            compilerCall.TargetFramework,
            compilerCall.IsCSharp,
            new Lazy<IReadOnlyCollection<string>>(() => args),
            ownerState: null);
        Assert.Throws<ArgumentException>(() => reader.ReadCommandLineArguments(compilerCall));
    }

}
