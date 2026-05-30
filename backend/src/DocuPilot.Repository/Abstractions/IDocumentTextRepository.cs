using DocuPilot.Models.Entities;

namespace DocuPilot.Repository.Abstractions;

/// <summary>
/// Data-access port for the <c>DocumentTexts</c> table (1:1 with Documents). Both the
/// interface and impl live in the Repository project (DA-011 §2.5). Re-extraction is
/// idempotent: <see cref="UpsertAsync"/> replaces the single row keyed by <c>DocumentId</c>,
/// never blind-inserts (the UNIQUE constraint is the backstop — DA-023 §P3.2.2).
/// </summary>
public interface IDocumentTextRepository
{
    /// <summary>
    /// Upserts the extracted-text row for a document by <c>DocumentId</c>: updates the
    /// existing row's <c>Content</c>/<c>CharCount</c>/<c>ExtractedAt</c> if present, otherwise
    /// adds a new row (with the supplied app-set <c>Id</c>). Stages the change on the tracked
    /// context — the caller commits it (within the status-transition transaction).
    /// </summary>
    Task UpsertAsync(DocumentText text, CancellationToken ct);

    /// <summary>Loads the extracted-text row for a document, or <c>null</c> if none exists.</summary>
    Task<DocumentText?> GetByDocumentIdAsync(Guid documentId, CancellationToken ct);
}
