namespace DocuPilot.Services.Abstractions;

/// <summary>
/// External-service port for producing a single text embedding (ADR §2). A DEDICATED port (NOT an
/// <c>EmbedAsync</c> bolted onto <see cref="ILlmClient"/>) — embedding and chat/generate are
/// different operations (text→vector vs prompt→text, different endpoint, different model). The
/// contract lives in Services; the HTTP implementation (<c>OllamaEmbeddingClient</c>) lives in
/// Infrastructure, keeping <c>HttpClient</c> and the provider wire format out of Services so the
/// embedding orchestrator depends only on this contract and unit tests can stub it with no network.
/// Ollama's native <c>/api/embeddings</c> is single-input, so the port is single-input and the
/// orchestrator loops per chunk (ADR §2).
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// Embeds a single text into a vector of length <see cref="Dimensions"/>.
    /// </summary>
    /// <exception cref="EmbeddingUnavailableException">
    /// The embedding service is unreachable / the model is not loaded / a per-call timeout fired (a
    /// transient, retryable-later fault — the orchestrator leaves the document <c>Classified</c>
    /// rather than marking it <c>Failed</c>, ADR §6).
    /// </exception>
    Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct);

    /// <summary>
    /// The configured model's vector dimensionality (e.g. 768 for <c>nomic-embed-text</c>). The
    /// single source of truth used to bootstrap + validate the Qdrant collection (ADR §3) — the
    /// collection's vector size MUST equal this.
    /// </summary>
    int Dimensions { get; }
}

/// <summary>An embedding response (ADR §2).</summary>
/// <param name="Vector">The embedding vector (length = <see cref="IEmbeddingClient.Dimensions"/>).</param>
/// <param name="Model">The model that produced the embedding.</param>
/// <param name="Duration">Wall-clock duration of the call.</param>
public sealed record EmbeddingResult(
    float[] Vector,
    string Model,
    TimeSpan Duration);

/// <summary>
/// Thrown by <see cref="IEmbeddingClient"/> for connection-level faults, "model not found", or a
/// per-call timeout — i.e. the embedding service itself is unavailable, NOT a per-document content
/// fault. The embedding orchestrator treats this as <b>transient</b>: it leaves the document
/// <c>Classified</c> (no <c>Failed</c>, nothing written to either store) so a temporarily-down
/// embedder does not poison the backlog (ADR §6 / PM Q4), mirroring <see cref="LlmUnavailableException"/>.
/// </summary>
public sealed class EmbeddingUnavailableException : Exception
{
    public EmbeddingUnavailableException(string message)
        : base(message)
    {
    }

    public EmbeddingUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
