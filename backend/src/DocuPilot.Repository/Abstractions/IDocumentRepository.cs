using DocuPilot.Models.Entities;

namespace DocuPilot.Repository.Abstractions;

/// <summary>
/// Data-access port for the <c>Documents</c> table. Both the interface and its
/// implementation live in the Repository project (DA-011 §2.5 — DB ports live here,
/// both sides). All <c>Documents</c> DB communication goes through this seam.
/// </summary>
public interface IDocumentRepository
{
    /// <summary>
    /// Adds a document row and persists it. The entity's <c>Id</c>, <c>Status</c>,
    /// <c>UploadedAt</c>, etc. are already set by the caller (the service).
    /// </summary>
    Task AddAsync(Document document, CancellationToken ct);

    /// <summary>
    /// Stages a new document row on the tracked context WITHOUT calling SaveChanges, so the
    /// caller can commit it atomically alongside other writes (e.g. the upload audit row) via
    /// <see cref="IUnitOfWork"/>.
    /// </summary>
    Task AddTrackedAsync(Document document, CancellationToken ct);

    /// <summary>
    /// Loads a single tracked document by id, or <c>null</c> if it does not exist.
    /// Tracked (not <c>AsNoTracking</c>) so the caller can mutate + persist it inside a
    /// transaction (status transitions). Used by detail/process flows.
    /// </summary>
    Task<Document?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Atomically claims the oldest <c>Queued</c> document, transitioning it to
    /// <c>ExtractingText</c> via a single guarded <c>ExecuteUpdateAsync</c>
    /// (<c>WHERE Id = @id AND Status = Queued</c>). Returns <c>true</c> if this call won the
    /// claim (affected rows = 1), <c>false</c> if another worker/iteration already claimed it.
    /// Used by the Worker (DA-025); exposed here so the claim is a single compare-and-swap.
    /// </summary>
    Task<bool> TryClaimAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Atomically claims a <c>TextExtracted</c> document for classification, transitioning it to
    /// <c>Classifying</c> via a single guarded <c>ExecuteUpdateAsync</c>
    /// (<c>WHERE Id = @id AND Status = TextExtracted</c>). Returns <c>true</c> if this call won the
    /// claim (affected rows = 1), <c>false</c> if another worker/iteration already claimed it.
    /// Phase-4 pass-2 claim (ADR §2) — the compare-and-swap analogue of <see cref="TryClaimAsync"/>.
    /// </summary>
    Task<bool> TryClaimForClassificationAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Atomically claims a <c>Classified</c> document for embedding, transitioning it to
    /// <c>GeneratingEmbeddings</c> via a single guarded <c>ExecuteUpdateAsync</c>
    /// (<c>WHERE Id = @id AND Status = Classified</c>). Returns <c>true</c> if this call won the
    /// claim (affected rows = 1), <c>false</c> otherwise. Phase-5 pass-3 claim (ADR §5) — the
    /// compare-and-swap analogue of <see cref="TryClaimForClassificationAsync"/>. Backed by
    /// <c>IX_Documents_Status</c> (no new index — DA-038 §P5.4).
    /// </summary>
    Task<bool> TryClaimForEmbeddingAsync(Guid id, CancellationToken ct);

    /// <summary>Persists pending changes on the tracked document(s). Used after mutating a tracked entity.</summary>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Returns the ids of the oldest <c>Queued</c> documents (FIFO by <c>UploadedAt ASC</c>),
    /// up to <paramref name="max"/>. Read-only / no-tracking selection used by the Worker poller
    /// (DA-025); the actual claim is the atomic <see cref="TryClaimAsync"/> on each id. Backed by
    /// <c>IX_Documents_Status</c>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetNextQueuedIdsAsync(int max, CancellationToken ct);

    /// <summary>
    /// Returns the ids of the oldest <c>TextExtracted</c> documents (FIFO by <c>UploadedAt ASC</c>),
    /// up to <paramref name="max"/>. Phase-4 pass-2 selection (DA-033) — the read-only/no-tracking
    /// analogue of <see cref="GetNextQueuedIdsAsync"/>; the actual claim is the atomic
    /// <see cref="TryClaimForClassificationAsync"/> done inside <c>ClassifyAsync</c> on each id.
    /// Backed by <c>IX_Documents_Status</c>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetNextTextExtractedIdsAsync(int max, CancellationToken ct);

    /// <summary>
    /// Stale-claim recovery (DA-024 §, PM Q4 — audit-timestamp, no <c>ClaimedAt</c> column):
    /// returns the ids of documents stuck in <c>ExtractingText</c> whose most-recent
    /// <c>ExtractionStarted</c> audit row is OLDER than <paramref name="cutoffUtc"/> (i.e. a
    /// crash/host-cancellation left them claimed but un-finished). The caller resets each to
    /// <c>Queued</c> with an audit row. Read-only / no-tracking; backed by
    /// <c>IX_Documents_Status</c> + <c>IX_AuditLogs_EntityId_CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetStaleExtractingIdsAsync(DateTime cutoffUtc, CancellationToken ct);

    /// <summary>
    /// Phase-4 stale-claim recovery (generalizes <see cref="GetStaleExtractingIdsAsync"/> for the
    /// classification stage): returns the ids of documents stuck in <c>Classifying</c> whose
    /// most-recent <c>ClassificationStarted</c> audit row is OLDER than <paramref name="cutoffUtc"/>
    /// (a crash/host-cancellation left them claimed). The caller resets each to <c>TextExtracted</c>.
    /// Read-only / no-tracking; backed by <c>IX_Documents_Status</c> + <c>IX_AuditLogs_EntityId_CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetStaleClassifyingIdsAsync(DateTime cutoffUtc, CancellationToken ct);

    /// <summary>
    /// Returns the ids of the oldest <c>Classified</c> documents (FIFO by <c>UploadedAt ASC</c>),
    /// up to <paramref name="max"/>. Phase-5 pass-3 selection (DA-040) — the read-only/no-tracking
    /// analogue of <see cref="GetNextTextExtractedIdsAsync"/>; the actual claim is the atomic
    /// <see cref="TryClaimForEmbeddingAsync"/> done inside <c>EmbedDocumentAsync</c> on each id.
    /// Backed by <c>IX_Documents_Status</c>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetNextClassifiedIdsAsync(int max, CancellationToken ct);

    /// <summary>
    /// Phase-5 stale-claim recovery (generalizes <see cref="GetStaleClassifyingIdsAsync"/> for the
    /// embedding stage): returns the ids of documents stuck in <c>GeneratingEmbeddings</c> whose
    /// most-recent <c>EmbeddingStarted</c> audit row is OLDER than <paramref name="cutoffUtc"/>
    /// (a crash/host-cancellation left them claimed). The caller resets each to <c>Classified</c>.
    /// Read-only / no-tracking; backed by <c>IX_Documents_Status</c> + <c>IX_AuditLogs_EntityId_CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetStaleGeneratingEmbeddingsIdsAsync(DateTime cutoffUtc, CancellationToken ct);

    /// <summary>
    /// Returns one page of documents ordered by <c>UploadedAt DESC</c> (newest first,
    /// backed by <c>IX_Documents_UploadedAt</c>) together with the total row count for
    /// pagination metadata.
    /// </summary>
    /// <param name="page">1-based page number (assumed already normalized by the caller).</param>
    /// <param name="pageSize">Page size (assumed already capped by the caller).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The page of entities and the total count across all pages.</returns>
    Task<(IReadOnlyList<Document> Items, long TotalCount)> ListAsync(int page, int pageSize, CancellationToken ct);
}
