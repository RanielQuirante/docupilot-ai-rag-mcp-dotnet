namespace DocuPilot.Services.Abstractions;

/// <summary>
/// Single dispatch entry point the processing orchestrator depends on. Holds the registered
/// <see cref="ITextExtractor"/> implementations and routes to the first whose <c>CanHandle</c>
/// matches; if none matches it throws <see cref="UnsupportedFormatException"/> (a
/// non-transient failure → the document goes <c>Failed</c>). Implemented in Infrastructure
/// (DA-011 §2.5, ADR §3).
/// </summary>
public interface ITextExtractionService
{
    /// <summary>
    /// Resolves the right extractor for the document and extracts its text.
    /// </summary>
    /// <exception cref="UnsupportedFormatException">No registered extractor can handle the format.</exception>
    Task<string> ExtractAsync(Stream content, string contentType, string fileName, CancellationToken ct);
}
