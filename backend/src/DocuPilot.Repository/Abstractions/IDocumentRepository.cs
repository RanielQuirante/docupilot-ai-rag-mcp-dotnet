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
