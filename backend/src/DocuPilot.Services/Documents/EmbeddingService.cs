using System.Text.Json;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuPilot.Services.Documents;

/// <summary>
/// Phase-5 embedding orchestrator (ADR §5/§6). Claims a <c>Classified</c> document
/// (atomic <c>Classified → GeneratingEmbeddings</c> CAS — we do NOT hold a DB transaction across the
/// slow embedding round-trips), chunks the extracted text, embeds each chunk into an in-memory
/// vector set, then enforces the dual-store write order: <b>Qdrant first</b> (delete-by-document +
/// batch upsert), <b>then</b> ONE <see cref="IUnitOfWork"/> transaction that replaces the SQL chunk
/// rows + flips <c>Status = ReadyForSearch</c> + audits. Status flips LAST, only after BOTH stores
/// succeed (the consistency invariant, ADR §6).
/// <para>
/// Embedder-down OR Qdrant-down ⇒ roll the claim back to <c>Classified</c>, write NOTHING, return
/// <see cref="ProcessingOutcome.Transient"/> (no backlog poisoning, PM Q4). A content fault
/// (no extracted text) ⇒ <c>Failed</c>. Idempotency: embed all chunks before touching either store,
/// delete-first in both stores, deterministic point ids — a re-run/crash-replay self-heals.
/// </para>
/// </summary>
public sealed class EmbeddingService : IEmbeddingService
{
    private const string DocumentEntityName = "Document";

    private readonly IDocumentRepository _documents;
    private readonly IDocumentTextRepository _texts;
    private readonly IDocumentChunkRepository _chunks;
    private readonly IAuditRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IChunkingService _chunker;
    private readonly IEmbeddingClient _embedder;
    private readonly IVectorStore _vectorStore;
    private readonly TimeProvider _timeProvider;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        IDocumentRepository documents,
        IDocumentTextRepository texts,
        IDocumentChunkRepository chunks,
        IAuditRepository audit,
        IUnitOfWork unitOfWork,
        IChunkingService chunker,
        IEmbeddingClient embedder,
        IVectorStore vectorStore,
        TimeProvider timeProvider,
        IOptions<EmbeddingOptions> options,
        ILogger<EmbeddingService> logger)
    {
        _documents = documents;
        _texts = texts;
        _chunks = chunks;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _chunker = chunker;
        _embedder = embedder;
        _vectorStore = vectorStore;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ProcessingOutcome> EmbedDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var document = await _documents.GetByIdAsync(documentId, ct);
        if (document is null)
        {
            return ProcessingOutcome.NotFound;
        }

        // Atomic claim: Classified → GeneratingEmbeddings (compare-and-swap). Lose → don't process.
        var claimed = await _documents.TryClaimForEmbeddingAsync(documentId, ct);
        if (!claimed)
        {
            _logger.LogDebug("Document {DocumentId} was not claimable for embedding (status {Status}).", documentId, document.Status);
            return ProcessingOutcome.NotClaimed;
        }

        // The tracked entity still reflects the pre-claim status; align it so persistence reasons
        // from the claimed state.
        document.Status = DocumentStatus.GeneratingEmbeddings;

        await WriteAuditAsync(documentId, AuditAction.EmbeddingStarted,
            JsonSerializer.Serialize(new { fromStatus = nameof(DocumentStatus.Classified), toStatus = nameof(DocumentStatus.GeneratingEmbeddings) }), ct);

        // Content fault: no extracted text to embed → Failed (not transient). Shouldn't happen for a
        // Classified doc, but null-guarded.
        var text = await _texts.GetByDocumentIdAsync(documentId, ct);
        if (text is null || string.IsNullOrWhiteSpace(text.Content))
        {
            await CompleteFailureAsync(document, "No extracted text available to embed.", ct);
            return ProcessingOutcome.Failed;
        }

        // Chunk (pure/deterministic). An all-whitespace-after-trim doc could yield zero chunks —
        // treat that as a content fault rather than persisting an empty, "ready" document.
        var chunkContents = _chunker.Chunk(text.Content);
        if (chunkContents.Count == 0)
        {
            await CompleteFailureAsync(document, "Document produced no chunks to embed.", ct);
            return ProcessingOutcome.Failed;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            // Embed EVERY chunk into an in-memory set BEFORE touching either store (so a fault
            // mid-loop leaves no half-written state — ADR §6 partial-embed handling).
            var (chunkRows, chunkVectors) = await EmbedAllAsync(document.Id, chunkContents, now, ct);

            // Dual-store write order: Qdrant FIRST (delete-by-document then batch upsert), THEN the
            // single SQL+status transaction. Status flips to ReadyForSearch only after BOTH succeed.
            await _vectorStore.DeleteByDocumentAsync(document.Id, ct);
            await _vectorStore.UpsertChunksAsync(chunkVectors, ct);

            await CompleteSuccessAsync(document, chunkRows, ct);
            return ProcessingOutcome.Succeeded;
        }
        catch (EmbeddingUnavailableException ex)
        {
            // TRANSIENT: the embedder is unavailable. Roll the claim back to Classified (NO Failed,
            // nothing written) so the Worker retries it later (ADR §6 / PM Q4).
            _logger.LogWarning(ex, "Embedder unavailable while embedding {DocumentId}; leaving document Classified for retry.", documentId);
            await RollbackClaimAsync(document, "Embedder unavailable — transient, retry later", ct);
            return ProcessingOutcome.Transient;
        }
        catch (VectorStoreUnavailableException ex)
        {
            // TRANSIENT: Qdrant is unavailable. Same roll-back-and-retry as the embedder-down path.
            _logger.LogWarning(ex, "Vector store unavailable while embedding {DocumentId}; leaving document Classified for retry.", documentId);
            await RollbackClaimAsync(document, "Vector store unavailable — transient, retry later", ct);
            return ProcessingOutcome.Transient;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown — leave the document GeneratingEmbeddings for stale-claim recovery (DA-040).
            throw;
        }
    }

    public async Task<int> RecoverStaleGeneratingEmbeddingsAsync(TimeSpan staleThreshold, CancellationToken ct)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - staleThreshold;
        var staleIds = await _documents.GetStaleGeneratingEmbeddingsIdsAsync(cutoff, ct);
        if (staleIds.Count == 0)
        {
            return 0;
        }

        var reset = 0;
        foreach (var id in staleIds)
        {
            ct.ThrowIfCancellationRequested();

            var document = await _documents.GetByIdAsync(id, ct);
            if (document is null || document.Status != DocumentStatus.GeneratingEmbeddings)
            {
                continue;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
            {
                document.Status = DocumentStatus.Classified;
                await _documents.SaveChangesAsync(innerCt);

                await _audit.AddAsync(BuildAudit(id, AuditAction.ReprocessQueued, now,
                    JsonSerializer.Serialize(new
                    {
                        fromStatus = nameof(DocumentStatus.GeneratingEmbeddings),
                        toStatus = nameof(DocumentStatus.Classified),
                        reason = "stale-claim recovery",
                    })), innerCt);
            }, ct);

            _logger.LogWarning("Reset stale claim for document {DocumentId} (stuck in GeneratingEmbeddings past threshold) back to Classified.", id);
            reset++;
        }

        return reset;
    }

    /// <summary>
    /// Embeds every chunk (per-chunk retry/timeout) into the in-memory chunk-row + chunk-vector sets.
    /// Throws <see cref="EmbeddingUnavailableException"/> if the embedder is down after retries — the
    /// caller treats it as transient and writes nothing.
    /// </summary>
    private async Task<(List<DocumentChunk> Rows, List<ChunkVector> Vectors)> EmbedAllAsync(
        Guid documentId, IReadOnlyList<DocumentChunkContent> chunkContents, DateTime now, CancellationToken ct)
    {
        var rows = new List<DocumentChunk>(chunkContents.Count);
        var vectors = new List<ChunkVector>(chunkContents.Count);

        foreach (var chunk in chunkContents)
        {
            ct.ThrowIfCancellationRequested();

            var embedding = await EmbedWithRetryAsync(chunk.Content, ct);

            var pointId = DeterministicPointId.For(documentId, chunk.ChunkIndex);
            var chunkId = Guid.CreateVersion7();

            rows.Add(new DocumentChunk
            {
                Id = chunkId,
                DocumentId = documentId,
                ChunkIndex = chunk.ChunkIndex,
                Content = chunk.Content,
                TokenEstimate = chunk.TokenEstimate,
                PointId = pointId,
                CreatedAt = now,
            });

            vectors.Add(new ChunkVector(
                PointId: pointId,
                Vector: embedding.Vector,
                DocumentId: documentId,
                ChunkId: chunkId,
                ChunkIndex: chunk.ChunkIndex,
                Snippet: Snippet(chunk.Content)));
        }

        return (rows, vectors);
    }

    /// <summary>One chunk embedding under a per-call timeout linked to the host token, with bounded transient retry.</summary>
    private async Task<EmbeddingResult> EmbedWithRetryAsync(string text, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _options.MaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                return await _embedder.EmbedAsync(text, linkedCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // host shutdown — propagate
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // Per-call timeout — transient. Retry, then surface as unavailable.
                if (attempt >= maxAttempts)
                {
                    throw new EmbeddingUnavailableException($"Embedding call timed out after {_options.TimeoutSeconds}s.");
                }
            }
            catch (EmbeddingUnavailableException) when (attempt < maxAttempts)
            {
                // Transient embedder fault — back off and retry.
            }

            await Task.Delay(250 * attempt, ct);
        }

        // Unreachable (the loop either returns or throws), but satisfies the compiler.
        throw new EmbeddingUnavailableException("Embedding failed after all retry attempts.");
    }

    private static string Snippet(string content)
    {
        const int max = 200;
        return content.Length <= max ? content : content[..max];
    }

    // ---- persistence ----

    private async Task CompleteSuccessAsync(Document document, List<DocumentChunk> chunkRows, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            // 1:N replace: delete prior chunk rows + insert the new ordered set (with PointId).
            await _chunks.ReplaceForDocumentAsync(document.Id, chunkRows, innerCt);

            document.Status = DocumentStatus.ReadyForSearch;
            document.ProcessedAt = now;
            document.FailureReason = null;
            await _documents.SaveChangesAsync(innerCt);

            await _audit.AddAsync(BuildAudit(document.Id, AuditAction.EmbeddingSucceeded, now,
                JsonSerializer.Serialize(new
                {
                    fromStatus = nameof(DocumentStatus.GeneratingEmbeddings),
                    toStatus = nameof(DocumentStatus.ReadyForSearch),
                    chunkCount = chunkRows.Count,
                    model = _options.Model,
                    dimensions = _embedder.Dimensions,
                })), innerCt);
        }, ct);
    }

    private async Task CompleteFailureAsync(Document document, string reason, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            document.Status = DocumentStatus.Failed;
            document.ProcessedAt = now;
            document.FailureReason = reason;
            await _documents.SaveChangesAsync(innerCt);

            await _audit.AddAsync(BuildAudit(document.Id, AuditAction.EmbeddingFailed, now,
                JsonSerializer.Serialize(new
                {
                    fromStatus = nameof(DocumentStatus.GeneratingEmbeddings),
                    toStatus = nameof(DocumentStatus.Failed),
                    error = reason,
                })), innerCt);
        }, ct);
    }

    /// <summary>Transient-down path: reset the claim GeneratingEmbeddings → Classified, with an audit row, no Failed, nothing else written.</summary>
    private async Task RollbackClaimAsync(Document document, string reason, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            document.Status = DocumentStatus.Classified;
            await _documents.SaveChangesAsync(innerCt);

            await _audit.AddAsync(BuildAudit(document.Id, AuditAction.ReprocessQueued, now,
                JsonSerializer.Serialize(new
                {
                    fromStatus = nameof(DocumentStatus.GeneratingEmbeddings),
                    toStatus = nameof(DocumentStatus.Classified),
                    reason,
                })), innerCt);
        }, ct);
    }

    private async Task WriteAuditAsync(Guid entityId, AuditAction action, string? detailsJson, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            await _audit.AddAsync(BuildAudit(entityId, action, now, detailsJson), innerCt);
        }, ct);
    }

    private static AuditLog BuildAudit(Guid entityId, AuditAction action, DateTime createdAt, string? detailsJson) => new()
    {
        Id = Guid.CreateVersion7(),
        EntityName = DocumentEntityName,
        EntityId = entityId,
        Action = action.ToString(),
        DetailsJson = detailsJson,
        CreatedAt = createdAt,
    };
}
