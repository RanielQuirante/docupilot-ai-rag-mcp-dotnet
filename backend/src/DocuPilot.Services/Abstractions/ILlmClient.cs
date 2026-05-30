namespace DocuPilot.Services.Abstractions;

/// <summary>
/// External-service port for a single-shot LLM completion. The contract lives in Services
/// (mirroring <see cref="ITextExtractor"/>/<see cref="IFileStorage"/>); the HTTP implementation
/// (<c>OllamaLlmClient</c>) lives in Infrastructure, keeping <c>HttpClient</c> and the provider
/// wire format out of the Services layer so the classification orchestrator depends only on this
/// contract and unit tests can stub it with no network (ADR §1).
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Sends a single-turn (system + user prompt) completion request and returns the raw model
    /// text. JSON-mode (<see cref="LlmRequest.JsonMode"/>) requests syntactically-valid JSON
    /// output but does NOT guarantee schema validity — the caller still validates fields/enums.
    /// </summary>
    /// <exception cref="LlmUnavailableException">
    /// The LLM is unreachable / the model is not loaded (a transient, retryable-later fault — the
    /// caller leaves the document in its pre-claim state rather than marking it Failed, ADR §6).
    /// </exception>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>A single-turn completion request (ADR §1).</summary>
/// <param name="Prompt">The user message / document payload.</param>
/// <param name="System">Optional system instruction.</param>
/// <param name="JsonMode">When true, requests structured JSON output (Ollama <c>format:json</c> / OpenAI <c>response_format</c>).</param>
/// <param name="Temperature">Sampling temperature; default 0 (greedy) for reproducibility.</param>
public sealed record LlmRequest(
    string Prompt,
    string? System = null,
    bool JsonMode = true,
    double Temperature = 0);

/// <summary>A completion response (ADR §1).</summary>
/// <param name="Content">The raw model text (expected to be a JSON object under JSON-mode).</param>
/// <param name="Model">The model that produced the response.</param>
/// <param name="Duration">Wall-clock duration of the call.</param>
public sealed record LlmResponse(
    string Content,
    string Model,
    TimeSpan Duration);

/// <summary>
/// Thrown by <see cref="ILlmClient"/> for connection-level faults or "model not found" — i.e. the
/// LLM service itself is unavailable, NOT a per-document content fault. The classification
/// orchestrator treats this as <b>transient</b>: it leaves the document in <c>TextExtracted</c>
/// (no <c>Failed</c>) so a temporarily-down LLM does not poison the backlog (ADR §6 / PM Q3).
/// </summary>
public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException(string message)
        : base(message)
    {
    }

    public LlmUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
