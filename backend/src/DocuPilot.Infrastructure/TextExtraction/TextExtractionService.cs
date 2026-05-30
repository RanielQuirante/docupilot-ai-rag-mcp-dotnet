using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;

namespace DocuPilot.Infrastructure.TextExtraction;

/// <summary>
/// Resolver / dispatch implementation of <see cref="ITextExtractionService"/>. Holds the
/// registered <see cref="ITextExtractor"/> implementations and routes to the first whose
/// <c>CanHandle</c> matches the document's content-type/filename. If none matches it throws
/// <see cref="UnsupportedFormatException"/> (a non-transient failure → the orchestrator marks
/// the document <c>Failed</c> with a clear reason). Adding a new format (e.g. an OCR extractor)
/// is a one-class addition with no orchestrator/Worker change (ADR §3).
/// </summary>
public sealed class TextExtractionService : ITextExtractionService
{
    private readonly IReadOnlyList<ITextExtractor> _extractors;

    public TextExtractionService(IEnumerable<ITextExtractor> extractors)
    {
        _extractors = extractors.ToList();
    }

    public async Task<string> ExtractAsync(Stream content, string contentType, string fileName, CancellationToken ct)
    {
        contentType ??= string.Empty;
        fileName ??= string.Empty;

        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(contentType, fileName))
            ?? throw new UnsupportedFormatException(
                $"No text extractor supports this document (contentType '{contentType}', file '{fileName}').");

        return await extractor.ExtractAsync(content, contentType, fileName, ct);
    }
}
