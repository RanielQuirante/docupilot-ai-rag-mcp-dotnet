namespace DocuPilot.Models.Contracts;

/// <summary>
/// Response for <c>GET /api/documents/{id}/text</c> — the full extracted plain text plus
/// its char count and extraction timestamp. Returned only when text has been extracted
/// (otherwise the endpoint returns 404).
/// </summary>
/// <param name="DocumentId">The owning document identifier.</param>
/// <param name="Content">The full extracted plain text.</param>
/// <param name="CharCount">Length of <see cref="Content"/>.</param>
/// <param name="ExtractedAt">Extraction timestamp (UTC).</param>
public sealed record DocumentTextResponse(
    Guid DocumentId,
    string Content,
    int CharCount,
    DateTime ExtractedAt);
