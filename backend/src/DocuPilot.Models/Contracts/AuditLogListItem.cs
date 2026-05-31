namespace DocuPilot.Models.Contracts;

/// <summary>
/// A single entry in the GLOBAL audit-log list (<c>GET /api/audit-logs</c>, spec §11.8),
/// returned newest-first inside a <see cref="PagedResult{T}"/>. Unlike the per-document
/// <see cref="AuditLogEntry"/> (which omits <c>EntityName</c> because it is always "Document"
/// there), the global list spans every entity type (Document / WorkflowTask / WorkflowTool),
/// so it carries <see cref="EntityName"/> + <see cref="EntityId"/> so the UI can render and link
/// each row. Read-only and additive (Phase 9, DA-058) — the frozen per-doc
/// <see cref="AuditLogEntry"/> shape is left untouched.
/// </summary>
/// <param name="Id">Audit row identifier.</param>
/// <param name="EntityName">The audited entity type: "Document", "WorkflowTask", or "WorkflowTool".</param>
/// <param name="EntityId">The GUID the event concerns (links to <c>/documents/:id</c> when <c>EntityName == "Document"</c>).</param>
/// <param name="Action">Event name (the <c>AuditAction</c> enum-name string, e.g. "ClassificationSucceeded", "ToolSucceeded").</param>
/// <param name="DetailsJson">Small JSON detail blob; may be null.</param>
/// <param name="CreatedAt">Event timestamp (UTC).</param>
public sealed record AuditLogListItem(
    Guid Id,
    string EntityName,
    Guid EntityId,
    string Action,
    string? DetailsJson,
    DateTime CreatedAt);
