using System.Text.Json;

namespace DocuPilot.Models.Contracts;

/// <summary>
/// Detail response for <c>GET /api/documents/{id}</c>. Extends the library-row fields with
/// the Phase-3 processing signals (<c>FailureReason</c>) and a lightweight extracted-text
/// summary (<c>CharCount</c>/<c>ExtractedAt</c>, present once text has been extracted — the
/// full text is fetched separately via <c>GET /api/documents/{id}/text</c> so this payload
/// stays small). Phase 4 adds nullable <c>Classification</c> (category + confidence + reason)
/// and <c>Metadata</c> (the parsed metadata JSON object) — both populated only once the document
/// is <c>Classified</c> (read paths are null-tolerant: the 1:1 children are 0-or-1 per document).
/// Deliberately omits <c>FilePath</c> (internal storage key).
/// </summary>
/// <param name="Id">Document identifier.</param>
/// <param name="FileName">Original user-supplied filename.</param>
/// <param name="ContentType">MIME type.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Status">Lifecycle status (enum name, e.g. "Queued", "TextExtracted", "Classifying", "Classified", "Failed").</param>
/// <param name="UploadedAt">Upload timestamp (UTC).</param>
/// <param name="ProcessedAt">Processing-complete timestamp (UTC); re-stamped on each terminal stage.</param>
/// <param name="FailureReason">Short human-readable failure summary; null unless Status is "Failed".</param>
/// <param name="CharCount">Extracted-text length; null until text is extracted.</param>
/// <param name="ExtractedAt">Extraction timestamp (UTC); null until text is extracted.</param>
/// <param name="Classification">The LLM classification (category + confidence + reason); null until classified.</param>
/// <param name="Metadata">The parsed extracted-metadata JSON object; null until classified (an empty extraction is the object <c>{}</c>).</param>
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
    DateTime? ExtractedAt,
    DocumentClassificationDto? Classification,
    JsonElement? Metadata);
