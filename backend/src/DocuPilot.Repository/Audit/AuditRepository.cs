using DocuPilot.Models.Entities;
using DocuPilot.Repository.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DocuPilot.Repository.Audit;

/// <summary>
/// EF Core implementation of <see cref="IAuditRepository"/>. Injects the base
/// <see cref="DbContext"/> (registered as the concrete <c>DocuPilotDbContext</c> in DI) so the
/// project stays provider-agnostic (DA-011 §2.3). Pure data access — no business logic.
/// </summary>
public sealed class AuditRepository : IAuditRepository
{
    private readonly DbContext _dbContext;

    public AuditRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(AuditLog auditLog, CancellationToken ct)
    {
        // Staged only — committed by the caller in the same transaction as the status change.
        await _dbContext.Set<AuditLog>().AddAsync(auditLog, ct);
    }

    public async Task<IReadOnlyList<AuditLog>> ListByEntityAsync(Guid entityId, CancellationToken ct)
    {
        return await _dbContext.Set<AuditLog>()
            .AsNoTracking()
            .Where(a => a.EntityId == entityId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<AuditLog> Items, long TotalCount)> ListAsync(
        int page, int pageSize, Guid? entityId, string? action, CancellationToken ct)
    {
        var query = _dbContext.Set<AuditLog>().AsNoTracking();

        if (entityId is { } id)
        {
            query = query.Where(a => a.EntityId == id);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action == action);
        }

        var totalCount = await query.LongCountAsync(ct);

        // Newest-first global timeline. An unfiltered ORDER BY CreatedAt is a scan+sort (the composite
        // index leads with EntityId) — acceptable at POC scale (DA-058). With entityId supplied the
        // query is covered by IX_AuditLogs_EntityId_CreatedAt.
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
