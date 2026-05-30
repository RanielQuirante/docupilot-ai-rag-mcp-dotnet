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
}
