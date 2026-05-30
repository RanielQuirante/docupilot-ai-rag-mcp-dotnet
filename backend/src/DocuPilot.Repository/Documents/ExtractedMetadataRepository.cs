using DocuPilot.Models.Entities;
using DocuPilot.Repository.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DocuPilot.Repository.Documents;

/// <summary>
/// EF Core implementation of <see cref="IExtractedMetadataRepository"/>. Injects the base
/// <see cref="DbContext"/> (registered as the concrete <c>DocuPilotDbContext</c> in DI) so the
/// project stays provider-agnostic (DA-011 §2.3). Pure data access — no business logic.
/// </summary>
public sealed class ExtractedMetadataRepository : IExtractedMetadataRepository
{
    private readonly DbContext _dbContext;

    public ExtractedMetadataRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task UpsertAsync(ExtractedMetadata metadata, CancellationToken ct)
    {
        // Select-by-DocumentId → update existing or insert new (idempotent upsert, DA-031 §P4.3.2).
        // Staged only — the orchestrator commits it inside the status-transition transaction.
        var existing = await _dbContext.Set<ExtractedMetadata>()
            .FirstOrDefaultAsync(m => m.DocumentId == metadata.DocumentId, ct);

        if (existing is null)
        {
            await _dbContext.Set<ExtractedMetadata>().AddAsync(metadata, ct);
        }
        else
        {
            existing.MetadataJson = metadata.MetadataJson;
            existing.Model = metadata.Model;
            existing.CreatedAt = metadata.CreatedAt;
        }
    }

    public async Task<ExtractedMetadata?> GetByDocumentIdAsync(Guid documentId, CancellationToken ct)
    {
        return await _dbContext.Set<ExtractedMetadata>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.DocumentId == documentId, ct);
    }
}
