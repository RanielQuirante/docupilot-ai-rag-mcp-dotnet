using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuPilot.Services.Search;

/// <summary>
/// Phase-6 semantic search orchestrator (DA-045). See <see cref="ISearchService"/>. Embed → vector
/// search → group-by-doc-keep-best → rank → filter → batch-hydrate → map. The
/// <see cref="IEmbeddingClient"/> and <see cref="IVectorStore"/> ports are stubbable so the whole
/// flow is unit-testable with no network. A down embedder/Qdrant ⇒ a clean
/// <see cref="SearchOutcome.Unavailable"/> (→ 503), never an unhandled 500.
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly IEmbeddingClient _embeddingClient;
    private readonly IVectorStore _vectorStore;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentChunkRepository _chunkRepository;
    private readonly IDocumentClassificationRepository _classificationRepository;
    private readonly SearchOptions _options;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        IEmbeddingClient embeddingClient,
        IVectorStore vectorStore,
        IDocumentRepository documentRepository,
        IDocumentChunkRepository chunkRepository,
        IDocumentClassificationRepository classificationRepository,
        IOptions<SearchOptions> options,
        ILogger<SearchService> logger)
    {
        _embeddingClient = embeddingClient;
        _vectorStore = vectorStore;
        _documentRepository = documentRepository;
        _chunkRepository = chunkRepository;
        _classificationRepository = classificationRepository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SearchOutcome> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        // 1) Validate. Empty/whitespace query is a client error → 400 (NOT a search that finds nothing).
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return SearchOutcome.EmptyQuery;
        }

        var text = query.Query.Trim();

        // Clamp the requested limit to [1, MaxLimit]; null/<=0 → DefaultLimit.
        var maxLimit = Math.Max(1, _options.MaxLimit);
        var defaultLimit = Math.Clamp(_options.DefaultLimit, 1, maxLimit);
        var limit = query.Limit is { } requested && requested > 0
            ? Math.Min(requested, maxLimit)
            : defaultLimit;

        // Over-fetch chunk hits so that, after collapsing same-doc chunks to one row, we still have
        // ≥ limit distinct documents. Bounded by MaxChunkFetch.
        var factor = Math.Max(1, _options.ChunkOverFetchFactor);
        var maxChunkFetch = Math.Max(limit, _options.MaxChunkFetch);
        var overFetch = Math.Min(limit * factor, maxChunkFetch);

        // 2) Embed the query with the SAME model that embedded the chunks (mandatory — cosine is
        // meaningless otherwise; guaranteed by reusing the one IEmbeddingClient). 3) Vector search.
        // A down embedder or down Qdrant → a clean Unavailable (→ 503), never a 500-storm.
        IReadOnlyList<ChunkHit> hits;
        try
        {
            var embedding = await _embeddingClient.EmbedAsync(text, ct);
            hits = await _vectorStore.SearchAsync(embedding.Vector, overFetch, documentId: null, ct);
        }
        catch (EmbeddingUnavailableException ex)
        {
            _logger.LogWarning(ex, "Semantic search unavailable: the embedder is down/warming.");
            return SearchOutcome.Unavailable;
        }
        catch (VectorStoreUnavailableException ex)
        {
            _logger.LogWarning(ex, "Semantic search unavailable: the vector store (Qdrant) is down.");
            return SearchOutcome.Unavailable;
        }

        if (hits.Count == 0)
        {
            return SearchOutcome.FromResults([]);
        }

        // 4) Group by documentId, keep the single best-scoring chunk per document, rank by that
        // score descending. (Best-passage relevance — a long doc of mediocre chunks must not
        // outrank a short, highly-relevant one.)
        var bestPerDoc = hits
            .GroupBy(h => h.DocumentId)
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .OrderByDescending(h => h.Score)
            .ToList();

        // Optional cosine floor (default 0 = off). Drop hits below the threshold when set.
        if (_options.MinScore > 0)
        {
            bestPerDoc = bestPerDoc
                .Where(h => h.Score >= _options.MinScore)
                .ToList();
        }

        // Take an over-allowance before hydration so the category filter (applied SQL-side on the
        // hydrated docs) can still yield up to `limit` rows after dropping non-matching categories.
        // The chunk-id set stays bounded by MaxChunkFetch.
        var candidates = bestPerDoc.Take(maxChunkFetch).ToList();

        // 5) Batch-hydrate (two id-set reads, NOT N+1): fileName + classification from Documents,
        // matchedText from DocumentChunks.Content.
        var documentIds = candidates.Select(h => h.DocumentId).Distinct().ToList();
        var chunkIds = candidates.Select(h => h.ChunkId).Distinct().ToList();

        var documents = await _documentRepository.GetByIdsAsync(documentIds, ct);
        var documentById = documents.ToDictionary(d => d.Id);

        var chunks = await _chunkRepository.GetByIdsAsync(chunkIds, ct);
        var contentByChunkId = chunks.ToDictionary(c => c.Id, c => c.Content);

        // Batch-load classifications for the candidate documents (one IN read; reuse the existing
        // category→display map). Done here rather than via a Documents join to avoid changing the
        // frozen GetByIdsAsync shape — classification is a 1:1 child read.
        var classificationByDoc = await LoadClassificationsAsync(documentIds, ct);

        var maxChars = Math.Max(1, _options.MatchedTextMaxChars);

        // Category filter: ignore gracefully unless it resolves to a KNOWN taxonomy category
        // (blank/null/off-taxonomy ⇒ no filter, per PM Q5). Normalize to the canonical display
        // string so the comparison is exact and case/format-tolerant.
        string? categoryFilter = null;
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var coerced = DocumentCategoryNames.Coerce(query.Category);
            // Coerce() maps off-list input to Unknown; treat a real-but-Unknown request as a filter
            // only when the caller literally asked for "Unknown", otherwise ignore it.
            var isExplicitUnknown = string.Equals(query.Category.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);
            if (coerced != DocumentCategory.Unknown || isExplicitUnknown)
            {
                categoryFilter = DocumentCategoryNames.ToDisplay(coerced);
            }
        }

        var results = new List<SearchResultModel>(limit);
        foreach (var hit in candidates)
        {
            // Drift guard: the vector search returned a document whose SQL row is gone → skip it.
            if (!documentById.TryGetValue(hit.DocumentId, out var document))
            {
                continue;
            }

            var classification = classificationByDoc.GetValueOrDefault(hit.DocumentId);

            // 5b) Category filter (SQL-side, on the hydrated docs). An unknown/blank category is
            // ignored gracefully (treated as no filter); a known one restricts the rows.
            if (categoryFilter is not null
                && !string.Equals(classification, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // matchedText: authoritative SQL Content of the winning chunk, trimmed; fall back to the
            // Qdrant snippet if the chunk row is missing (rare SQL↔Qdrant drift).
            var matchedText = contentByChunkId.TryGetValue(hit.ChunkId, out var content) && !string.IsNullOrEmpty(content)
                ? Trim(content, maxChars)
                : Trim(hit.Snippet ?? string.Empty, maxChars);

            results.Add(new SearchResultModel(
                document.Id,
                document.FileName,
                classification,
                hit.Score,
                matchedText,
                hit.ChunkIndex));

            if (results.Count >= limit)
            {
                break;
            }
        }

        return SearchOutcome.FromResults(results);
    }

    /// <summary>
    /// Resolves the classification display string per document via a single id-set read against the
    /// classification repository, mapping the stored category to its spec display string.
    /// </summary>
    private async Task<Dictionary<Guid, string>> LoadClassificationsAsync(
        IReadOnlyCollection<Guid> documentIds,
        CancellationToken ct)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var classifications = await _classificationRepository.GetByDocumentIdsAsync(documentIds, ct);
        return classifications.ToDictionary(
            c => c.DocumentId,
            c => DocumentCategoryNames.ToDisplay(c.Classification));
    }

    private static string Trim(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}
