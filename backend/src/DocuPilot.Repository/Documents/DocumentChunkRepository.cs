using DocuPilot.Models.Entities;
using DocuPilot.Repository.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DocuPilot.Repository.Documents;

/// <summary>
/// EF Core implementation of <see cref="IDocumentChunkRepository"/>. Injects the base
/// <see cref="DbContext"/> (registered as the concrete <c>DocuPilotDbContext</c> in DI) so the
/// project stays provider-agnostic (DA-011 §2.3). Pure data access — no business logic. The 1:N
/// replace (delete-by-DocumentId + insert ordered set) is the idempotent re-embed primitive
/// (DA-038 §P5.2.2); the composite <c>UNIQUE(DocumentId, ChunkIndex)</c> is the DB-level backstop.
/// </summary>
public sealed class DocumentChunkRepository : IDocumentChunkRepository
{
    private readonly DbContext _dbContext;

    public DocumentChunkRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ReplaceForDocumentAsync(Guid documentId, IReadOnlyList<DocumentChunk> chunks, CancellationToken ct)
    {
        // 1:N replace: stage delete of all prior rows for the doc, then stage the new ordered set.
        // Staged on the tracked context — the orchestrator commits it atomically alongside the
        // status flip + audit (one IUnitOfWork transaction, ADR §6). Tracked load (not no-tracking)
        // so EF issues the deletes within the same transaction.
        var existing = await _dbContext.Set<DocumentChunk>()
            .Where(c => c.DocumentId == documentId)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            _dbContext.Set<DocumentChunk>().RemoveRange(existing);
        }

        if (chunks.Count > 0)
        {
            await _dbContext.Set<DocumentChunk>().AddRangeAsync(chunks, ct);
        }
    }

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var existing = await _dbContext.Set<DocumentChunk>()
            .Where(c => c.DocumentId == documentId)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            _dbContext.Set<DocumentChunk>().RemoveRange(existing);
        }
    }

    public async Task<IReadOnlyList<DocumentChunk>> GetByDocumentIdAsync(Guid documentId, CancellationToken ct)
    {
        return await _dbContext.Set<DocumentChunk>()
            .AsNoTracking()
            .Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.ChunkIndex)
            .ToListAsync(ct);
    }

    public async Task<int> CountByDocumentIdAsync(Guid documentId, CancellationToken ct)
    {
        return await _dbContext.Set<DocumentChunk>()
            .AsNoTracking()
            .CountAsync(c => c.DocumentId == documentId, ct);
    }

    public async Task<IReadOnlyList<DocumentChunk>> GetByIdsAsync(IReadOnlyCollection<Guid> chunkIds, CancellationToken ct)
    {
        if (chunkIds.Count == 0)
        {
            return [];
        }

        // Single WHERE Id IN (@chunkIds) primary-key seek — no N+1 (Phase-6 matchedText hydration,
        // DA-045). No-tracking: a pure read of the winning chunks' authoritative Content.
        return await _dbContext.Set<DocumentChunk>()
            .AsNoTracking()
            .Where(c => chunkIds.Contains(c.Id))
            .ToListAsync(ct);
    }
}
