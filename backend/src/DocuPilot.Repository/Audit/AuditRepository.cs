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
}
