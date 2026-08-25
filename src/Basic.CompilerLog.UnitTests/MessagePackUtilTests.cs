using Basic.CompilerLog.Util;
using Basic.CompilerLog.Util.Serialize;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Basic.CompilerLog.UnitTests;

public sealed class MessagePackUtilTests
{
    /// <summary>
    /// Ids deliberately out of order, and with a pair that differs only by case so that an ordinal
    /// sort is distinguishable from a culture aware one.
    /// </summary>
    private static readonly string[] DiagnosticIds =
    [
        "CS8321", "CS0219", "IDE0005", "CA1001", "cs1591", "CS1591", "CA2007", "IDE0060", "CS0168", "CA1822"
    ];

    private static CSharpCompilationOptions CreateOptions() =>
        new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            specificDiagnosticOptions: DiagnosticIds.Select(x => new KeyValuePair<string, ReportDiagnostic>(x, ReportDiagnostic.Suppress)));

    /// <summary>
    /// MessagePack writes a dictionary as a map in enumeration order and
    /// <see cref="System.Collections.Immutable.ImmutableDictionary{TKey, TValue}"/> enumerates in
    /// hash order, which .NET seeds per process. Serializing one of those puts the same options on
    /// the wire in a different order in every process that writes a log, so the order is checked
    /// directly here: repeating a conversion within one process cannot see it.
    /// </summary>
    [Fact]
    public void SpecificDiagnosticOptionsAreOrdinalSorted()
    {
        var (pack, _) = MessagePackUtil.CreateCSharpCompilationOptionsPack(CreateOptions());
        Assert.Equal(
            DiagnosticIds.OrderBy(x => x, StringComparer.Ordinal),
            pack.SpecificDiagnosticOptions!.Keys);
    }

    [Fact]
    public void SpecificDiagnosticOptionsRoundTrip()
    {
        var options = CreateOptions();
        var (pack, csharpPack) = MessagePackUtil.CreateCSharpCompilationOptionsPack(options);
        var cancellationToken = TestContext.Current.CancellationToken;
        var bytes = MessagePackSerializer.Serialize(pack, CommonUtil.SerializerOptions, cancellationToken);
        var readPack = MessagePackSerializer.Deserialize<CompilationOptionsPack>(bytes, CommonUtil.SerializerOptions, cancellationToken);
        var readOptions = MessagePackUtil.CreateCSharpCompilationOptions(readPack, csharpPack);
        Assert.Equal(options.SpecificDiagnosticOptions, readOptions.SpecificDiagnosticOptions);
    }
}
