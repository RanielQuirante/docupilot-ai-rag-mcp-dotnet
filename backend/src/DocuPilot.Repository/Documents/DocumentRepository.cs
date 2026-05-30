using DocuPilot.Models.Entities;
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
