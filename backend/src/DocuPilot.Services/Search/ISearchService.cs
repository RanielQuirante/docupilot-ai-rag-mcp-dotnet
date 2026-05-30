namespace DocuPilot.Services.Search;

/// <summary>
/// Layer-agnostic input to <see cref="ISearchService.SearchAsync"/> (keeps Services free of the API
/// <c>SearchRequest</c> DTO — mirrors <c>DocumentUploadInput</c>). The controller adapts the bound
/// request into this.
/// </summary>
/// <param name="Query">The natural-language search text (validated by the service).</param>
/// <param name="Limit">Requested document limit; <c>null</c> ⇒ <c>Search:DefaultLimit</c>. Clamped to <c>[1, MaxLimit]</c>.</param>
/// <param name="Category">Optional classification display-string filter; <c>null</c>/blank ⇒ no filter.</param>
public sealed record SearchQuery(string Query, int? Limit = null, string? Category = null);

/// <summary>
/// A single document-level search result at the Services layer (mapped to the Contracts
/// <c>SearchResult</c> by the controller). Field-for-field with the wire DTO but kept in Services so
/// the entity never leaks and the API DTO stays an API concern.
/// </summary>
public sealed record SearchResultModel(
    Guid DocumentId,
    string FileName,
    string? Classification,
    float Score,
    string MatchedText,
    int ChunkIndex);

/// <summary>
/// The discriminated outcome of a search. The controller maps each kind to a status code WITHOUT
/// throwing for the expected states: <see cref="SearchOutcomeKind.Results"/> → <c>200</c>,
/// <see cref="SearchOutcomeKind.EmptyQuery"/> → <c>400</c>, <see cref="SearchOutcomeKind.Unavailable"/>
/// → <c>503</c>. A genuinely unexpected exception still bubbles to a <c>500</c> (real bug).
/// </summary>
public enum SearchOutcomeKind
{
    /// <summary>The search ran; <see cref="SearchOutcome.Results"/> holds the ranked rows (possibly empty → still a 200).</summary>
    Results,

    /// <summary>The query was empty/whitespace — a client error (→ 400).</summary>
    EmptyQuery,

    /// <summary>The embedder or Qdrant was unavailable on the synchronous read path — retryable (→ 503).</summary>
    Unavailable,
}

/// <summary>
/// The result of <see cref="ISearchService.SearchAsync"/>. Use the factory members; the controller
/// switches on <see cref="Kind"/>.
/// </summary>
public sealed class SearchOutcome
{
    private SearchOutcome(SearchOutcomeKind kind, IReadOnlyList<SearchResultModel> results)
    {
        Kind = kind;
        Results = results;
    }

    /// <summary>Which outcome occurred (drives the controller's status-code mapping).</summary>
    public SearchOutcomeKind Kind { get; }

    /// <summary>The ranked results when <see cref="Kind"/> is <see cref="SearchOutcomeKind.Results"/>; otherwise empty.</summary>
    public IReadOnlyList<SearchResultModel> Results { get; }

    /// <summary>A successful search (possibly with zero rows → still a 200).</summary>
    public static SearchOutcome FromResults(IReadOnlyList<SearchResultModel> results) =>
        new(SearchOutcomeKind.Results, results);

    /// <summary>The query was empty/whitespace (→ 400).</summary>
    public static SearchOutcome EmptyQuery { get; } = new(SearchOutcomeKind.EmptyQuery, []);

    /// <summary>The embedder or Qdrant was down (→ 503 with Retry-After).</summary>
    public static SearchOutcome Unavailable { get; } = new(SearchOutcomeKind.Unavailable, []);
}

/// <summary>
/// Phase-6 semantic search orchestrator (DA-045). Embeds the natural-language query via
/// <c>IEmbeddingClient</c>, runs a cosine vector search over the document-chunk collection via
/// <c>IVectorStore.SearchAsync</c>, groups the chunk hits by document keeping the best-scoring chunk
/// per document, ranks by that score, applies the optional score floor + category filter, and
/// batch-hydrates <c>fileName</c>/<c>classification</c> (from <c>Documents</c>) + <c>matchedText</c>
/// (from <c>DocumentChunks.Content</c>) in two id-set reads (no N+1). A down embedder or down Qdrant
/// surfaces as <see cref="SearchOutcome.Unavailable"/> (→ 503), never an unhandled 500. Pure read —
/// no write path, no schema change. Exposed as a service so the thin <c>SearchController</c> just
/// binds → delegates → maps, and the flow is unit-testable with stubs (no network).
/// </summary>
public interface ISearchService
{
    /// <summary>Runs a semantic search and returns a discriminated <see cref="SearchOutcome"/>.</summary>
    Task<SearchOutcome> SearchAsync(SearchQuery query, CancellationToken ct);
}
