namespace DocuPilot.Models.Contracts;

/// <summary>
/// Classification view returned in the detail payload and by
/// <c>GET /api/documents/{id}/classification</c>. <c>Category</c> is the spec display string
/// (e.g. "Employee Record"), one of the fixed 8-value taxonomy. Null on the detail DTO until the
/// document has been classified.
/// </summary>
/// <param name="Category">The category display string (e.g. "Contract", "Invoice", "Unknown").</param>
/// <param name="Confidence">Model confidence in [0,1].</param>
/// <param name="Reason">The model's short justification; may be null.</param>
/// <param name="Model">The model that produced it (provenance); may be null.</param>
/// <param name="CreatedAt">Classification timestamp (UTC).</param>
public sealed record DocumentClassificationDto(
    string Category,
    decimal Confidence,
    string? Reason,
    string? Model,
    DateTime CreatedAt);
