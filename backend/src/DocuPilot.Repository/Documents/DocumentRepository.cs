using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DocuPilot.Repository.Documents;

/// <summary>
/// EF Core implementation of <see cref="IDocumentRepository"/>. Wraps the application's
/// <see cref="DbContext"/> (the concrete <c>DocuPilotDbContext</c> lives in Infrastructure
/// and is registered as the base <see cref="DbContext"/> in DI, so this project stays
/// provider-agnostic and references only <c>Microsoft.EntityFrameworkCore</c> — DA-011 §2.3/§2.7).
/// Pure data access — no business logic.
/// </summary>
public sealed class DocumentRepository : IDocumentRepository
{
    private readonly DbContext _dbContext;

    public DocumentRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Document document, CancellationToken ct)
    {
        await _dbContext.Set<Document>().AddAsync(document, ct);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task AddTrackedAsync(Document document, CancellationToken ct)
    {
        await _dbContext.Set<Document>().AddAsync(document, ct);
    }

    public async Task<Document?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await _dbContext.Set<Document>().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task<bool> TryClaimAsync(Guid id, CancellationToken ct)
    {
        // Single-statement compare-and-swap: only flips Queued → ExtractingText. The
        // WHERE Status = Queued guard is the optimistic concurrency check — if another
        // worker already claimed the row, affected rows is 0 and we return false.
        var affected = await _dbContext.Set<Document>()
            .Where(d => d.Id == id && d.Status == DocumentStatus.Queued)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(d => d.Status, DocumentStatus.ExtractingText),
                ct);

        return affected == 1;
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetNextQueuedIdsAsync(int max, CancellationToken ct)
    {
        if (max <= 0)
        {
            return [];
        }

        // FIFO fairness: oldest Queued first (UploadedAt ASC), backed by IX_Documents_Status.
        // No-tracking projection of just the id — the claim itself is the atomic TryClaimAsync.
        return await _dbContext.Set<Document>()
            .AsNoTracking()
            .Where(d => d.Status == DocumentStatus.Queued)
            .OrderBy(d => d.UploadedAt)
            .Take(max)
            .Select(d => d.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetStaleExtractingIdsAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        // Documents stuck in ExtractingText whose latest ExtractionStarted audit is older than
        // the cutoff (PM Q4 — audit-timestamp, no ClaimedAt column). A row qualifies when the
        // MAX(CreatedAt) of its ExtractionStarted audit rows < cutoff. Documents with no
        // ExtractionStarted audit at all (shouldn't happen, but be safe) also qualify so they
        // can never be stranded. Provider-agnostic LINQ → SQL.
        const string startedAction = nameof(AuditAction.ExtractionStarted);

        var auditLogs = _dbContext.Set<AuditLog>().AsNoTracking();

        return await _dbContext.Set<Document>()
            .AsNoTracking()
            .Where(d => d.Status == DocumentStatus.ExtractingText)
            .Where(d => !auditLogs.Any(a =>
                a.EntityId == d.Id
                && a.Action == startedAction
                && a.CreatedAt >= cutoffUtc))
            .Select(d => d.Id)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<Document> Items, long TotalCount)> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var query = _dbContext.Set<Document>().AsNoTracking();

        var totalCount = await query.LongCountAsync(ct);

        var items = await query
            .OrderByDescending(d => d.UploadedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
