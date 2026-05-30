using DocuPilot.Repository.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DocuPilot.Repository;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>. Injects the base <see cref="DbContext"/>
/// (the same scoped instance the repositories share within a request/scope) so staged changes
/// from multiple repositories commit together. Provider-agnostic — references only
/// <c>Microsoft.EntityFrameworkCore</c> (DA-011 §2.3).
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly DbContext _dbContext;

    public UnitOfWork(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        // Use the provider's execution strategy so transient-fault retries (if configured)
        // re-run the whole transactional block atomically rather than mid-transaction.
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction = await _dbContext.Database.BeginTransactionAsync(ct);
            await action(ct);
            await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }
}
