using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DocuPilot.Repository.Documents;

/// <summary>
/// EF Core implementation of <see cref="IWorkflowTaskRepository"/>. Injects the base
/// <see cref="DbContext"/> (registered as the concrete <c>DocuPilotDbContext</c> in DI) so the
/// project stays provider-agnostic (DA-011 §2.3). Pure data access — no business logic. The create
/// is stage-only (committed by <see cref="IUnitOfWork"/> alongside the audit row).
/// </summary>
public sealed class WorkflowTaskRepository : IWorkflowTaskRepository
{
    private readonly DbContext _dbContext;

    public WorkflowTaskRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddTrackedAsync(WorkflowTask task, CancellationToken ct)
    {
        // Staged only — committed by the caller in the same transaction as the create audit row.
        await _dbContext.Set<WorkflowTask>().AddAsync(task, ct);
    }

    public async Task<WorkflowTask?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await _dbContext.Set<WorkflowTask>()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<IReadOnlyList<WorkflowTask>> ListAsync(WorkflowTaskStatus? status, Guid? documentId, CancellationToken ct)
    {
        var query = _dbContext.Set<WorkflowTask>().AsNoTracking();

        if (status is { } s)
        {
            query = query.Where(t => t.Status == s);
        }

        if (documentId is { } docId)
        {
            query = query.Where(t => t.DocumentId == docId);
        }

        return await query
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<int> CountOpenByDocumentAsync(Guid documentId, string taskType, CancellationToken ct)
    {
        return await _dbContext.Set<WorkflowTask>()
            .AsNoTracking()
            .CountAsync(
                t => t.DocumentId == documentId
                     && t.Status == WorkflowTaskStatus.Open
                     && t.TaskType == taskType,
                ct);
    }

    public async Task<int> CountByStatusAsync(WorkflowTaskStatus status, CancellationToken ct)
    {
        // Single server-side COUNT, backed by IX_WorkflowTasks_Status (DA-058 dashboard stats).
        return await _dbContext.Set<WorkflowTask>()
            .AsNoTracking()
            .CountAsync(t => t.Status == status, ct);
    }
}
