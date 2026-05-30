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
}
