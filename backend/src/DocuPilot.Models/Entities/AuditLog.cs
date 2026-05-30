namespace DocuPilot.Models.Entities;

/// <summary>
/// Persistence entity for an append-only audit event (table <c>AuditLogs</c>, spec §9.6).
/// An immutable event trail that outlives the entity it describes — <see cref="EntityId"/>
/// is deliberately NOT a foreign key (DA-023 §P3.3). A plain POCO; all mapping (column
/// types, the composite <c>(EntityId, CreatedAt DESC)</c> index) is fluent in
/// <c>AuditLogConfiguration</c>.
/// </summary>
public sealed class AuditLog
{
    /// <summary>Primary key. App-generated via <c>Guid.CreateVersion7()</c> — no DB-side default.</summary>
    public Guid Id { get; set; }

    /// <summary>The audited entity type. <c>"Document"</c> for all Phase-3 events.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>The <c>Documents.Id</c> the event concerns. Plain indexed GUID — NOT an FK.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Event name (the <c>AuditAction</c> enum name string, e.g. "ExtractionStarted").</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Small JSON detail blob (status transition, attempt count, error/exception text). Nullable.</summary>
    public string? DetailsJson { get; set; }

    /// <summary>Event timestamp (UTC), set via <c>TimeProvider</c>.</summary>
    public DateTime CreatedAt { get; set; }
}
