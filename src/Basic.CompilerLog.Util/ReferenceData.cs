using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Basic.CompilerLog.Util;

public sealed class ReferenceData(string filePath, Guid mvid, byte[] imageBytes)
{
    /// <summary>
    /// The file path for the given reference.
    /// </summary>
    /// <remarks>
    /// This path is only valid on the machine where the log was generated. It's 
    /// generally only useful for inforamtional diagnostics.
    /// </remarks>
    public string FilePath { get; } = filePath;
    public Guid Mvid { get; } = mvid;
    public byte[] ImageBytes { get; } = imageBytes;

    public string FileName => Path.GetFileName(FilePath);

    [ExcludeFromCodeCoverage]
    public override string ToString() => $"{FileName} {Mvid}";
}