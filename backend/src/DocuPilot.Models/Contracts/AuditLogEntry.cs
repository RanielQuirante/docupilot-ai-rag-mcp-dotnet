namespace DocuPilot.Models.Contracts;

/// <summary>
/// A single entry in a document's audit timeline (<c>GET /api/documents/{id}/audit</c>),
/// returned newest-first. Mirrors the <c>AuditLogs</c> row minus the internal <c>EntityName</c>
/// (always "Document" for this endpoint).
/// </summary>
/// <param name="Id">Audit row identifier.</param>
/// <param name="Action">Event name (e.g. "Queued", "ExtractionStarted", "ExtractionSucceeded").</param>
/// <param name="DetailsJson">Small JSON detail blob (status transition, attempt, error); may be null.</param>
/// <param name="CreatedAt">Event timestamp (UTC).</param>
public sealed record AuditLogEntry(
    Guid Id,
    string Action,
    string? DetailsJson,
    DateTime CreatedAt);
