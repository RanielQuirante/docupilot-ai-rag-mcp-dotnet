using DocuPilot.Models.Entities;

namespace DocuPilot.Repository.Abstractions;

/// <summary>
/// Data-access port for the append-only <c>AuditLogs</c> table (DA-011 reserved the
/// Repository <c>Audit/</c> folder for this). Audit rows are written in the SAME transaction
/// as the status change they record (DA-023 §P3.8 constraint #6).
/// </summary>
public interface IAuditRepository
{
    /// <summary>
    /// Stages an audit row on the tracked context (does NOT call SaveChanges) so the caller
    /// can commit it atomically with the status transition it records.
    /// </summary>
    Task AddAsync(AuditLog auditLog, CancellationToken ct);

    /// <summary>
    /// Returns a document's audit timeline newest-first (<c>WHERE EntityId = @id ORDER BY
    /// CreatedAt DESC</c>), backed by <c>IX_AuditLogs_EntityId_CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<AuditLog>> ListByEntityAsync(Guid entityId, CancellationToken ct);

    /// <summary>
    /// Global audit-log list (Phase 9, DA-058): returns one page of audit rows ordered
    /// <c>CreatedAt DESC</c> (newest-first) together with the total matching row count for pagination.
    /// Optional filters: <paramref name="entityId"/> (when supplied the query is covered by
    /// <c>IX_AuditLogs_EntityId_CreatedAt</c>) and <paramref name="action"/> (the <c>AuditAction</c>
    /// enum-name string; the caller validates it before calling). No-tracking — a pure read.
    /// </summary>
    /// <param name="page">1-based page number (assumed already normalized by the caller).</param>
    /// <param name="pageSize">Page size (assumed already capped by the caller).</param>
    /// <param name="entityId">Optional entity-id filter (<c>null</c> ⇒ no filter on this dimension).</param>
    /// <param name="action">Optional action-name filter (<c>null</c> ⇒ no filter on this dimension).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(IReadOnlyList<AuditLog> Items, long TotalCount)> ListAsync(
        int page, int pageSize, Guid? entityId, string? action, CancellationToken ct);
}
