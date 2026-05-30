using DocuPilot.Models.Entities;
using DocuPilot.Repository.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DocuPilot.Repository.Documents;

/// <summary>
/// EF Core implementation of <see cref="IDocumentTextRepository"/>. Injects the base
/// <see cref="DbContext"/> (registered as the concrete <c>DocuPilotDbContext</c> in DI) so the
/// project stays provider-agnostic (DA-011 §2.3). Pure data access — no business logic.
/// </summary>
public sealed class DocumentTextRepository : IDocumentTextRepository
{
    private readonly DbContext _dbContext;

    public DocumentTextRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task UpsertAsync(DocumentText text, CancellationToken ct)
    {
        // Select-by-DocumentId → update existing or insert new (idempotent upsert, DA-023 §P3.2.2).
        // Does NOT call SaveChanges — the change is staged on the tracked context so the
        // orchestrator commits it atomically alongside the status + audit writes.
        var existing = await _dbContext.Set<DocumentText>()
            .FirstOrDefaultAsync(t => t.DocumentId == text.DocumentId, ct);

        if (existing is null)
        {
            await _dbContext.Set<DocumentText>().AddAsync(text, ct);
        }
        else
        {
            existing.Content = text.Content;
            existing.CharCount = text.CharCount;
            existing.ExtractedAt = text.ExtractedAt;
        }
    }

    public async Task<DocumentText?> GetByDocumentIdAsync(Guid documentId, CancellationToken ct)
    {
        return await _dbContext.Set<DocumentText>()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.DocumentId == documentId, ct);
    }
}
