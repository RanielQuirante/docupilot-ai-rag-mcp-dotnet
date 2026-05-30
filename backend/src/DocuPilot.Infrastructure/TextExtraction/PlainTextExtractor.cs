using System.Text;
using DocuPilot.Services.Abstractions;

namespace DocuPilot.Infrastructure.TextExtraction;

/// <summary>
/// Extracts text from <c>.txt</c> / <c>text/plain</c> files using the built-in
/// <see cref="StreamReader"/> (BOM-aware encoding detection, UTF-8 default). Zero dependency.
/// </summary>
public sealed class PlainTextExtractor : ITextExtractor
{
    public bool CanHandle(string contentType, string fileName) =>
        HasExtension(fileName, ".txt") ||
        contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(Stream content, string contentType, string fileName, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    internal static bool HasExtension(string fileName, string ext) =>
        Path.GetExtension(fileName).Equals(ext, StringComparison.OrdinalIgnoreCase);
}
