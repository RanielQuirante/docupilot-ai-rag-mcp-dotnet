using DocuPilot.Models.Contracts;
using DocuPilot.Services.Common;

namespace DocuPilot.Services.Documents;

/// <summary>
/// Business-logic surface for the document upload + library use cases. The controller
/// is thin — it adapts the HTTP request into <see cref="DocumentUploadInput"/> values and
/// calls these methods; all validation, storage, persistence, and entity↔contract mapping
/// happen here. The <c>Document</c> entity never leaves this layer.
/// </summary>
public interface IDocumentService
{
    /// <summary>
    /// Validates and persists a batch of uploaded files. Each file is processed
    /// independently: successes land in <see cref="UploadDocumentResponse.Uploaded"/>,
    /// rejections in <see cref="UploadDocumentResponse.Failed"/>.
    /// </summary>
    Task<UploadDocumentResponse> UploadAsync(IReadOnlyList<DocumentUploadInput> files, CancellationToken ct);

    /// <summary>
    /// Returns a page of documents newest-first as a <see cref="PagedResult{T}"/> of
    /// <see cref="DocumentListItem"/>. <paramref name="page"/>/<paramref name="pageSize"/>
    /// are normalized here (page ≥ 1, pageSize within [1, 100]).
    /// </summary>
    Task<PagedResult<DocumentListItem>> ListAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Returns the detail view for a document (<c>GET /api/documents/{id}</c>) — metadata +
    /// status + failure reason + extracted-text summary (char count / extracted-at). Returns
    /// <c>null</c> if the document does not exist (controller maps to 404).
    /// </summary>
    Task<DocumentDetail?> GetDetailAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Returns the full extracted text for a document (<c>GET /api/documents/{id}/text</c>),
    /// or <c>null</c> if the document or its text row does not exist (controller maps to 404).
    /// </summary>
    Task<DocumentTextResponse?> GetTextAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Returns a document's audit timeline newest-first (<c>GET /api/documents/{id}/audit</c>).
    /// Returns an empty list for a document with no events (or one that doesn't exist).
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetAuditAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Returns the classification for a document (<c>GET /api/documents/{id}/classification</c>),
    /// or <c>null</c> if the document has not been classified yet (controller maps to 404).
    /// </summary>
    Task<DocumentClassificationDto?> GetClassificationAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Returns the extracted metadata for a document (<c>GET /api/documents/{id}/metadata</c>) with
    /// the stored JSON parsed into a real object, or <c>null</c> if not yet extracted (404).
    /// </summary>
    Task<DocumentMetadataResponse?> GetMetadataAsync(Guid id, CancellationToken ct);
}
