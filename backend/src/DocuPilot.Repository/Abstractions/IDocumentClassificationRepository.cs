using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;

namespace DocuPilot.Repository.Abstractions;

/// <summary>
/// Data-access port for the <c>DocumentClassifications</c> table (1:1 with Documents). Both the
/// interface and impl live in the Repository project (DA-011 §2.5). Re-classification is
/// idempotent: <see cref="UpsertAsync"/> replaces the single row keyed by <c>DocumentId</c>,
/// never blind-inserts (the UNIQUE constraint is the backstop — DA-031 §P4.2.2).
/// </summary>
public interface IDocumentClassificationRepository
{
    /// <summary>
    /// Upserts the classification row for a document by <c>DocumentId</c>: updates the existing
    /// row's category/confidence/reason/model/created-at if present, otherwise adds a new row
    /// (with the supplied app-set <c>Id</c>). Stages the change on the tracked context — the
    /// caller commits it within the status-transition transaction (via <see cref="IUnitOfWork"/>).
    /// </summary>
    Task UpsertAsync(DocumentClassification classification, CancellationToken ct);

    /// <summary>Loads the classification row for a document, or <c>null</c> if none exists (read paths are 0-or-1 tolerant).</summary>
    Task<DocumentClassification?> GetByDocumentIdAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Batch-loads the classification rows for a set of document ids (no-tracking) so the library
    /// list can carry the category without an N+1. Documents without a classification are simply
    /// absent from the result.
    /// </summary>
    Task<IReadOnlyList<DocumentClassification>> GetByDocumentIdsAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken ct);

    /// <summary>
    /// Dashboard aggregate (Phase 9, DA-058): a single <c>GROUP BY Classification</c> over
    /// <c>DocumentClassifications</c> returning the document count per <see cref="DocumentCategory"/>
    /// that has at least one classified row. Read-only / no row materialization (no N+1). Categories
    /// with zero classified documents are absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<DocumentCategory, int>> CountByCategoryAsync(CancellationToken ct);
}
