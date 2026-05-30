using DocuPilot.Models.Entities;

namespace DocuPilot.Repository.Abstractions;

/// <summary>
/// Data-access port for the <c>DocumentChunks</c> table — the FIRST 1:N child of Documents (DA-038).
/// Both the interface and impl live in the Repository project (DA-011 §2.5). Re-embed is idempotent:
/// the persist is a <b>1:N replace</b> — delete-all-by-<c>DocumentId</c> then insert the new ordered
/// set — never a blind insert and never a per-row select-then-update (the composite
/// <c>UNIQUE(DocumentId, ChunkIndex)</c> is the DB-level backstop — DA-038 §P5.2.2). All write methods
/// stage on the tracked context; the caller commits within the status-transition transaction (via
/// <c>IUnitOfWork</c>).
/// </summary>
public interface IDocumentChunkRepository
{
    /// <summary>
    /// Replaces the document's chunk set: stages a delete of all existing rows for
    /// <paramref name="documentId"/> then stages the inserts of <paramref name="chunks"/> (the
    /// ordered new set, each carrying its app-set <c>Id</c> + <c>PointId</c>). Does NOT call
    /// SaveChanges — the caller commits it inside the embed transaction.
    /// </summary>
    Task ReplaceForDocumentAsync(Guid documentId, IReadOnlyList<DocumentChunk> chunks, CancellationToken ct);

    /// <summary>Stages a delete of all chunk rows for a document (no SaveChanges). Used by the re-embed/idempotency path.</summary>
    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct);

    /// <summary>Loads all chunk rows for a document ordered by <c>ChunkIndex</c> (no-tracking). Used by Phase-6/7 retrieval.</summary>
    Task<IReadOnlyList<DocumentChunk>> GetByDocumentIdAsync(Guid documentId, CancellationToken ct);

    /// <summary>Counts the chunk rows for a document (no-tracking). Backs the detail DTO's <c>ChunkCount</c> (DA-039 §g).</summary>
    Task<int> CountByDocumentIdAsync(Guid documentId, CancellationToken ct);
}
