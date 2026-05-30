namespace DocuPilot.Models.Contracts;

/// <summary>
/// A single row in the document library list response (<c>GET /api/documents</c>).
/// Deliberately omits <c>FilePath</c> (internal storage key — not exposed to clients).
/// </summary>
/// <param name="Id">Document identifier.</param>
/// <param name="FileName">Original user-supplied filename.</param>
/// <param name="ContentType">MIME type.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Status">Lifecycle status (enum name, e.g. "Uploaded").</param>
/// <param name="UploadedAt">Upload timestamp (UTC).</param>
/// <param name="ProcessedAt">Processing-complete timestamp (UTC); null until Phase 3.</param>
public sealed record DocumentListItem(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    DateTime UploadedAt,
    DateTime? ProcessedAt);
