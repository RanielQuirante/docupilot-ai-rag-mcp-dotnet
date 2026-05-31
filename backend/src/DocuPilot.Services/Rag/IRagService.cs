namespace DocuPilot.Services.Rag;

/// <summary>
/// Layer-agnostic input to <see cref="IRagService.AskAsync"/> (keeps Services free of the API
/// <c>AskRequest</c> DTO — mirrors <c>SearchQuery</c>). The controller adapts the bound request into
/// this.
/// </summary>
/// <param name="Question">The natural-language question (validated by the service).</param>
/// <param name="TopK">Requested chunk count; <c>null</c> ⇒ <c>Rag:TopK</c>. Clamped to <c>[1, MaxTopK]</c>.</param>
/// <param name="Category">Optional classification display-string filter; <c>null</c>/blank ⇒ no filter.</param>
public sealed record AskQuery(string Question, int? TopK = null, string? Category = null);

/// <summary>
/// The RAG answer at the Services layer (mapped to the Contracts <c>AskResponse</c> by the
/// controller). Kept in Services so the entity never leaks and the API DTO stays an API concern.
/// </summary>
/// <param name="Answer">The LLM-generated grounded answer text (prose), OR the canned not-found string.</param>
/// <param name="AnswerFound">
/// <c>false</c> ⇒ the canned not-found path (empty/low-score retrieval OR the model returned the
/// canned string); <c>true</c> ⇒ a real grounded answer.
/// </param>
/// <param name="Citations">The retrieved chunks fed as context, ranked by score (empty when not found).</param>
public sealed record AskResultModel(
    string Answer,
    bool AnswerFound,
    IReadOnlyList<CitationModel> Citations);

/// <summary>
/// A single source citation at the Services layer (mapped to the Contracts <c>Citation</c>). The
/// retrieved chunk the answer was grounded in (spec §5.10 trust target).
/// </summary>
/// <param name="DocumentId">Owning document (links to <c>/documents/:id</c>).</param>
/// <param name="FileName">The document's original filename.</param>
/// <param name="ChunkIndex">0-based chunk index within the document.</param>
/// <param name="Page">ALWAYS <c>null</c> for the POC — chunks don't persist page numbers (ADR §2/§3, documented N/A).</param>
/// <param name="Score">The chunk's cosine similarity score (retrieval relevance).</param>
/// <param name="Snippet">The chunk's authoritative SQL <c>Content</c>, trimmed to <c>Rag:SnippetMaxChars</c>.</param>
public sealed record CitationModel(
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    int? Page,
    float Score,
    string Snippet);

/// <summary>
/// The discriminated outcome of an ask. The controller maps each kind to a status code WITHOUT
/// throwing for the expected states: <see cref="AskOutcomeKind.Answer"/> → <c>200</c>,
/// <see cref="AskOutcomeKind.EmptyQuestion"/> → <c>400</c>, <see cref="AskOutcomeKind.Unavailable"/>
/// → <c>503</c>. A genuinely unexpected exception still bubbles to a <c>500</c> (real bug). Mirrors
/// <c>SearchOutcome</c> (ADR §3).
/// </summary>
public enum AskOutcomeKind
{
    /// <summary>The ask ran; <see cref="AskOutcome.Result"/> holds the answer + citations (incl. the not-found case → still a 200).</summary>
    Answer,

    /// <summary>The question was empty/whitespace — a client error (→ 400).</summary>
    EmptyQuestion,

    /// <summary>The embedder, Qdrant, OR the chat LLM was unavailable on the synchronous read path — retryable (→ 503).</summary>
    Unavailable,
}

/// <summary>
/// The result of <see cref="IRagService.AskAsync"/>. Use the factory members; the controller switches
/// on <see cref="Kind"/>.
/// </summary>
public sealed class AskOutcome
{
    private AskOutcome(AskOutcomeKind kind, AskResultModel? result)
    {
        Kind = kind;
        Result = result;
    }

    /// <summary>Which outcome occurred (drives the controller's status-code mapping).</summary>
    public AskOutcomeKind Kind { get; }

    /// <summary>The answer + citations when <see cref="Kind"/> is <see cref="AskOutcomeKind.Answer"/>; otherwise <c>null</c>.</summary>
    public AskResultModel? Result { get; }

    /// <summary>A successful ask (incl. the not-found case → still a 200).</summary>
    public static AskOutcome FromResult(AskResultModel result) =>
        new(AskOutcomeKind.Answer, result);

    /// <summary>The question was empty/whitespace (→ 400).</summary>
    public static AskOutcome EmptyQuestion { get; } = new(AskOutcomeKind.EmptyQuestion, null);

    /// <summary>The embedder, Qdrant, or the chat LLM was down (→ 503 with Retry-After).</summary>
    public static AskOutcome Unavailable { get; } = new(AskOutcomeKind.Unavailable, null);
}

/// <summary>
/// Phase-7 RAG question-answering orchestrator (DA-049, spec §5.9). Runs the five steps: embed the
/// question (<c>IEmbeddingClient</c>) → cosine vector search top-k chunks (<c>IVectorStore.SearchAsync</c>)
/// → SHORT-CIRCUIT the canned not-found answer on empty / all-below-<c>Rag:MinScore</c> retrieval
/// (WITHOUT calling the LLM — the grounding guard) → batch-hydrate the authoritative
/// <c>DocumentChunks.Content</c> + <c>Documents.FileName</c>/classification (two id-set reads, no N+1)
/// → build the score-ordered, budget-capped, source-labeled context block → call the chat LLM in
/// PROSE mode (<c>JsonMode=false</c>, <c>Temperature=0</c>) with the VERBATIM mandatory grounding
/// system instruction (§5.9) + the §13.3 prompt → parse the answer + detect the model's own canned
/// not-found phrase. A down embedder / Qdrant / chat LLM ⇒ a clean <see cref="AskOutcome.Unavailable"/>
/// (→ 503), never an unhandled 500. Pure read — no write path, no schema change. Exposed as a service
/// so the thin <c>AskController</c> just binds → delegates → maps, and the flow is unit-testable with
/// stubs (no network).
/// </summary>
public interface IRagService
{
    /// <summary>Runs a RAG ask and returns a discriminated <see cref="AskOutcome"/>.</summary>
    Task<AskOutcome> AskAsync(AskQuery query, CancellationToken ct);
}
