using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DocuPilot.Repository.Documents;

/// <summary>
/// EF Core implementation of <see cref="IDocumentClassificationRepository"/>. Injects the base
/// <see cref="DbContext"/> (registered as the concrete <c>DocuPilotDbContext</c> in DI) so the
/// project stays provider-agnostic (DA-011 §2.3). Pure data access — no business logic.
/// </summary>
public sealed class DocumentClassificationRepository : IDocumentClassificationRepository
{
    private readonly DbContext _dbContext;

    public DocumentClassificationRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task UpsertAsync(DocumentClassification classification, CancellationToken ct)
    {
        // Select-by-DocumentId → update existing or insert new (idempotent upsert, DA-031 §P4.2.2).
        // Does NOT call SaveChanges — staged on the tracked context so the orchestrator commits it
        // atomically alongside the metadata + status + audit writes.
        var existing = await _dbContext.Set<DocumentClassification>()
            .FirstOrDefaultAsync(c => c.DocumentId == classification.DocumentId, ct);

        if (existing is null)
        {
            await _dbContext.Set<DocumentClassification>().AddAsync(classification, ct);
        }
        else
        {
            existing.Classification = classification.Classification;
            existing.Confidence = classification.Confidence;
            existing.Reason = classification.Reason;
            existing.Model = classification.Model;
            existing.CreatedAt = classification.CreatedAt;
        }
    }

    public async Task<DocumentClassification?> GetByDocumentIdAsync(Guid documentId, CancellationToken ct)
    {
        return await _dbContext.Set<DocumentClassification>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.DocumentId == documentId, ct);
    }

    public async Task<IReadOnlyList<DocumentClassification>> GetByDocumentIdsAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        return await _dbContext.Set<DocumentClassification>()
            .AsNoTracking()
            .Where(c => documentIds.Contains(c.DocumentId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<DocumentCategory, int>> CountByCategoryAsync(CancellationToken ct)
    {
        // Single GROUP BY Classification aggregate — no rows materialized, server-side COUNT
        // (DA-058 dashboard stats). Categories with zero classified rows are absent.
        var grouped = await _dbContext.Set<DocumentClassification>()
            .AsNoTracking()
            .GroupBy(c => c.Classification)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return grouped.ToDictionary(x => x.Category, x => x.Count);
    }
}
