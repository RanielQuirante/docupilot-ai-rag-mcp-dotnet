using DocuPilot.Models.Entities;

namespace DocuPilot.Repository.Abstractions;

/// <summary>
/// Data-access port for the <c>ExtractedMetadata</c> table (1:1 with Documents). Both the
/// interface and impl live in the Repository project (DA-011 §2.5). Re-extraction is idempotent:
/// <see cref="UpsertAsync"/> replaces the single row keyed by <c>DocumentId</c>, never
/// blind-inserts (the UNIQUE constraint is the backstop — DA-031 §P4.3.2).
/// </summary>
public interface IExtractedMetadataRepository
{
    /// <summary>
    /// Upserts the metadata row for a document by <c>DocumentId</c>: updates the existing row's
    /// json/model/created-at if present, otherwise adds a new row (with the supplied app-set
    /// <c>Id</c>). Stages the change on the tracked context — the caller commits it within the
    /// status-transition transaction (via <see cref="IUnitOfWork"/>).
    /// </summary>
    Task UpsertAsync(ExtractedMetadata metadata, CancellationToken ct);

    /// <summary>Loads the metadata row for a document, or <c>null</c> if none exists (read paths are 0-or-1 tolerant).</summary>
    Task<ExtractedMetadata?> GetByDocumentIdAsync(Guid documentId, CancellationToken ct);
}
