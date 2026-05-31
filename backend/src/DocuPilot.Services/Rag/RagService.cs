using System.Text;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuPilot.Services.Rag;

/// <summary>
/// Phase-7 RAG question-answering orchestrator (DA-049). See <see cref="IRagService"/>. Embed →
/// vector-search top-k chunks → short-circuit not-found on empty/below-<c>Rag:MinScore</c> (NO LLM
/// call) → batch-hydrate authoritative SQL text → build the score-ordered, budget-capped, labeled
/// context block → call the chat LLM in PROSE mode (<c>JsonMode=false</c>, <c>Temperature=0</c>) with
/// the VERBATIM grounding system instruction → parse + detect the model's canned not-found phrase. A
/// down embedder/Qdrant/LLM ⇒ a clean <see cref="AskOutcome.Unavailable"/> (→ 503). The ports are
/// stubbable so the whole flow is unit-testable with no network.
/// </summary>
public sealed class RagService : IRagService
{
    private readonly IEmbeddingClient _embeddingClient;
    private readonly IVectorStore _vectorStore;
    private readonly ILlmClient _llm;
    private readonly IPromptProvider _prompts;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentChunkRepository _chunkRepository;
    private readonly IDocumentClassificationRepository _classificationRepository;
    private readonly RagOptions _options;
    private readonly LlmOptions _llmOptions;
    private readonly ILogger<RagService> _logger;

    public RagService(
        IEmbeddingClient embeddingClient,
        IVectorStore vectorStore,
        ILlmClient llm,
        IPromptProvider prompts,
        IDocumentRepository documentRepository,
        IDocumentChunkRepository chunkRepository,
        IDocumentClassificationRepository classificationRepository,
        IOptions<RagOptions> options,
        IOptions<LlmOptions> llmOptions,
        ILogger<RagService> logger)
    {
        _embeddingClient = embeddingClient;
        _vectorStore = vectorStore;
        _llm = llm;
        _prompts = prompts;
        _documentRepository = documentRepository;
        _chunkRepository = chunkRepository;
        _classificationRepository = classificationRepository;
        _options = options.Value;
        _llmOptions = llmOptions.Value;
        _logger = logger;
    }

    public async Task<AskOutcome> AskAsync(AskQuery query, CancellationToken ct)
    {
        // 1) Validate. Empty/whitespace question is a client error → 400 (NOT an ask that finds nothing).
        if (string.IsNullOrWhiteSpace(query.Question))
        {
            return AskOutcome.EmptyQuestion;
        }

        var question = query.Question.Trim();

        // Clamp topK to [1, MaxTopK]; null/<=0 → TopK.
        var maxTopK = Math.Max(1, _options.MaxTopK);
        var defaultTopK = Math.Clamp(_options.TopK, 1, maxTopK);
        var topK = query.TopK is { } requested && requested > 0
            ? Math.Min(requested, maxTopK)
            : defaultTopK;

        // 2) (§5.9 step 1) Embed the question with the SAME model that embedded the chunks (mandatory).
        // 3) (§5.9 steps 2–3) Vector search top-k CHUNK hits. A down embedder/Qdrant → Unavailable (503).
        IReadOnlyList<ChunkHit> hits;
        try
        {
            var embedding = await _embeddingClient.EmbedAsync(question, ct);
            hits = await _vectorStore.SearchAsync(embedding.Vector, topK, documentId: null, ct);
        }
        catch (EmbeddingUnavailableException ex)
        {
            _logger.LogWarning(ex, "Ask unavailable: the embedder is down/warming.");
            return AskOutcome.Unavailable;
        }
        catch (VectorStoreUnavailableException ex)
        {
            _logger.LogWarning(ex, "Ask unavailable: the vector store (Qdrant) is down.");
            return AskOutcome.Unavailable;
        }

        // Rank hits by score descending (RAG keeps raw chunk hits — multiple chunks per doc are
        // desirable, unlike Phase-6 doc-collapsed search).
        var ranked = hits.OrderByDescending(h => h.Score).ToList();

        // 4) GROUNDING GUARD — SHORT-CIRCUIT before the LLM: empty retrieval OR every hit below the
        // floor ⇒ the canned not-found answer WITHOUT calling the LLM (cheaper + removes the
        // hallucination opportunity, ADR §4).
        if (ranked.Count == 0 || ranked.All(h => h.Score < _options.MinScore))
        {
            _logger.LogInformation(
                "Ask short-circuited to not-found (no chunk met the relevance floor {MinScore}); LLM not called.",
                _options.MinScore);
            return AskOutcome.FromResult(NotFound());
        }

        // 5) (§5.9 step 4) Hydrate authoritative text + filename/classification (two id-set reads, no N+1).
        var documentIds = ranked.Select(h => h.DocumentId).Distinct().ToList();
        var chunkIds = ranked.Select(h => h.ChunkId).Distinct().ToList();

        var documents = await _documentRepository.GetByIdsAsync(documentIds, ct);
        var documentById = documents.ToDictionary(d => d.Id);

        var chunks = await _chunkRepository.GetByIdsAsync(chunkIds, ct);
        var contentByChunkId = chunks.ToDictionary(c => c.Id, c => c.Content);

        var classificationByDoc = await LoadClassificationsAsync(documentIds, ct);

        var categoryFilter = ResolveCategoryFilter(query.Category);

        // Materialize the surviving, hydrated, ranked chunks (drift guard + optional category filter).
        var snippetMax = Math.Max(1, _options.SnippetMaxChars);
        var perChunkMax = Math.Max(1, _options.PerChunkMaxChars);

        var retrieved = new List<RetrievedChunk>(ranked.Count);
        foreach (var hit in ranked)
        {
            // Drift guard: the vector search returned a document whose SQL row is gone → skip it.
            if (!documentById.TryGetValue(hit.DocumentId, out var document))
            {
                continue;
            }

            var classification = classificationByDoc.GetValueOrDefault(hit.DocumentId);

            // Optional category filter (SQL-side, on the hydrated docs).
            if (categoryFilter is not null
                && !string.Equals(classification, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Authoritative SQL Content of the chunk; fall back to the Qdrant snippet on SQL↔Qdrant drift.
            var content = contentByChunkId.TryGetValue(hit.ChunkId, out var c) && !string.IsNullOrEmpty(c)
                ? c
                : hit.Snippet ?? string.Empty;

            retrieved.Add(new RetrievedChunk(
                document.Id,
                document.FileName,
                hit.ChunkIndex,
                hit.Score,
                content));
        }

        // If the category filter (or drift) emptied the set → short-circuit not-found (step-4 semantics).
        if (retrieved.Count == 0)
        {
            return AskOutcome.FromResult(NotFound());
        }

        // Build the labeled, budget-capped context block (score-ordered; drop lowest-score chunks if
        // over budget — they still cite). retrieved is already score-desc (ranked from hits).
        var contextBlock = BuildContextBlock(retrieved, perChunkMax, Math.Max(1, _options.ContextMaxChars));

        // 6) (§5.9 step 5) Generate the answer — PROSE mode (JsonMode=false), temp 0, verbatim grounding
        // system instruction + the §13.3 prompt. A down/timed-out LLM → Unavailable (503).
        string answerText;
        try
        {
            var prompt = _prompts.BuildRagPrompt(question, contextBlock);
            var response = await CallLlmAsync(_prompts.RagGroundingSystemPrompt, prompt, ct);
            answerText = response.Content?.Trim() ?? string.Empty;
        }
        catch (LlmUnavailableException ex)
        {
            _logger.LogWarning(ex, "Ask unavailable: the chat LLM is down/warming/timed-out.");
            return AskOutcome.Unavailable;
        }

        // 7) Parse + detect the model's own canned not-found phrase (case-insensitive contains). If the
        // model says it can't answer, surface not-found with empty citations (citing chunks would mislead).
        if (string.IsNullOrWhiteSpace(answerText) || ContainsNotFoundPhrase(answerText))
        {
            return AskOutcome.FromResult(NotFound());
        }

        // Cite ALL retrieved chunks fed as context, ranked by score (do NOT parse which the model used).
        var citations = retrieved
            .Select(r => new CitationModel(
                r.DocumentId,
                r.FileName,
                r.ChunkIndex,
                Page: null, // ALWAYS null for the POC — chunks don't persist page numbers (ADR §2/§3).
                r.Score,
                Trim(r.Content, snippetMax)))
            .ToList();

        return AskOutcome.FromResult(new AskResultModel(answerText, AnswerFound: true, citations));
    }

    /// <summary>The canned not-found result (empty citations) — both short-circuit and post-LLM paths.</summary>
    private AskResultModel NotFound() =>
        new(_prompts.RagNotFoundAnswer, AnswerFound: false, []);

    /// <summary>Case-insensitive contains-match on the canonical canned not-found phrase.</summary>
    private bool ContainsNotFoundPhrase(string answer) =>
        answer.Contains(_prompts.RagNotFoundAnswer, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the labeled context block: each surviving chunk tagged <c>[Source N] (file: ..., chunk: ...)</c>,
    /// concatenated in score order until adding the next chunk would exceed <paramref name="contextMaxChars"/>.
    /// Each chunk's content is hard-capped at <paramref name="perChunkMaxChars"/> first.
    /// </summary>
    private static string BuildContextBlock(
        IReadOnlyList<RetrievedChunk> retrieved,
        int perChunkMaxChars,
        int contextMaxChars)
    {
        var sb = new StringBuilder();
        var source = 0;
        foreach (var chunk in retrieved)
        {
            source++;
            var body = Trim(chunk.Content, perChunkMaxChars);
            var block = $"[Source {source}] (file: {chunk.FileName}, chunk: {chunk.ChunkIndex})\n{body}";

            // Stop once adding this block would overrun the budget (but always include the first/top
            // chunk so the model has at least the most-relevant passage to ground on).
            var separator = sb.Length > 0 ? "\n\n" : string.Empty;
            if (sb.Length > 0 && sb.Length + separator.Length + block.Length > contextMaxChars)
            {
                break;
            }

            sb.Append(separator).Append(block);
        }

        return sb.ToString();
    }

    /// <summary>Single LLM call in PROSE mode under a per-call timeout linked to the request token (ADR §6).</summary>
    private async Task<LlmResponse> CallLlmAsync(string system, string prompt, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_llmOptions.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await _llm.CompleteAsync(
                new LlmRequest(prompt, system, JsonMode: false, Temperature: 0),
                linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation — propagate
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Per-call timeout — treat as transient unavailability (→ 503, like Phase-4/6).
            throw new LlmUnavailableException($"RAG LLM call timed out after {_llmOptions.TimeoutSeconds}s.");
        }
    }

    /// <summary>Resolves the optional category filter to a canonical display string (blank/off-taxonomy ⇒ no filter).</summary>
    private static string? ResolveCategoryFilter(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        var coerced = DocumentCategoryNames.Coerce(category);
        var isExplicitUnknown = string.Equals(category.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);
        return coerced != DocumentCategory.Unknown || isExplicitUnknown
            ? DocumentCategoryNames.ToDisplay(coerced)
            : null;
    }

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

    /// <summary>A hydrated, ranked chunk ready for the context block + citation.</summary>
    private readonly record struct RetrievedChunk(
        Guid DocumentId,
        string FileName,
        int ChunkIndex,
        float Score,
        string Content);
}
