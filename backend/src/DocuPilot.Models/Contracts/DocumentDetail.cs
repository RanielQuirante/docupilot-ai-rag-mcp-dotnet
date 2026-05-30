namespace DocuPilot.Models.Contracts;

/// <summary>
/// Detail response for <c>GET /api/documents/{id}</c>. Extends the library-row fields with
/// the Phase-3 processing signals (<c>FailureReason</c>) and a lightweight extracted-text
/// summary (<c>CharCount</c>/<c>ExtractedAt</c>, present once text has been extracted — the
/// full text is fetched separately via <c>GET /api/documents/{id}/text</c> so this payload
/// stays small). Deliberately omits <c>FilePath</c> (internal storage key).
/// </summary>
/// <param name="Id">Document identifier.</param>
/// <param name="FileName">Original user-supplied filename.</param>
/// <param name="ContentType">MIME type.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Status">Lifecycle status (enum name, e.g. "Queued", "TextExtracted", "Failed").</param>
/// <param name="UploadedAt">Upload timestamp (UTC).</param>
/// <param name="ProcessedAt">Processing-complete timestamp (UTC); set on either terminal Phase-3 state.</param>
/// <param name="FailureReason">Short human-readable failure summary; null unless Status is "Failed".</param>
/// <param name="CharCount">Extracted-text length; null until text is extracted.</param>
/// <param name="ExtractedAt">Extraction timestamp (UTC); null until text is extracted.</param>
public sealed record DocumentDetail(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    DateTime UploadedAt,
    DateTime? ProcessedAt,
    string? FailureReason,
    int? CharCount,
    DateTime? ExtractedAt);
