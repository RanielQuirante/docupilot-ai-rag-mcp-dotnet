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
/// <param name="FailureReason">Human-readable failure reason; populated only when <c>Status == Failed</c>, otherwise null.</param>
/// <param name="Classification">The category display string (e.g. "Invoice"); null until the document is classified. Confidence/metadata are detail-only — the list stays lean (ADR §7).</param>
public sealed record DocumentListItem(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    DateTime UploadedAt,
    DateTime? ProcessedAt,
    string? FailureReason,
    string? Classification);
