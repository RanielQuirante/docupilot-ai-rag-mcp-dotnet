namespace DocuPilot.Models.Contracts;

/// <summary>
/// Response body for <c>POST /api/documents/upload</c>. Each file in the batch is
/// validated and persisted independently, so a partial failure is reportable without
/// failing the whole request: successful files appear in <see cref="Uploaded"/> and
/// rejected ones in <see cref="Failed"/>.
/// </summary>
/// <param name="Uploaded">Files that were stored and persisted.</param>
/// <param name="Failed">Files that were rejected, each with a reason.</param>
public sealed record UploadDocumentResponse(
    IReadOnlyList<UploadedDocument> Uploaded,
    IReadOnlyList<FailedDocument> Failed);
