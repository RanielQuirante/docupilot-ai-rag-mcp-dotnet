namespace DocuPilot.Services.Abstractions;

/// <summary>
/// External-service port for extracting plain text from a document stream. The interface
/// (contract) lives in Services; the per-format implementations (<c>.txt</c>/<c>.pdf</c>/
/// <c>.docx</c>) and the resolver live in Infrastructure (DA-011 §2.5, ADR §3), keeping the
/// third-party extraction libraries out of the Services layer.
/// </summary>
public interface ITextExtractor
{
    /// <summary>
    /// True if this extractor can handle the given content-type / filename. Used by the
    /// resolver to dispatch to the right per-format implementation.
    /// </summary>
    bool CanHandle(string contentType, string fileName);

    /// <summary>
    /// Extracts plain text from <paramref name="content"/>. Implementations should honor
    /// <paramref name="ct"/> (the orchestrator links it to a per-document timeout CTS).
    /// </summary>
    Task<string> ExtractAsync(Stream content, string contentType, string fileName, CancellationToken ct);
}
