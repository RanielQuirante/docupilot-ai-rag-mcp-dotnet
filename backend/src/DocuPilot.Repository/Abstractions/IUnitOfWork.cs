namespace DocuPilot.Repository.Abstractions;

/// <summary>
/// Transactional commit seam exposed to the Services layer so the processing orchestrator
/// can group several repository writes (upsert text + set status + insert audit) into ONE
/// atomic commit without referencing EF Core or the concrete DbContext (DA-011 §2.3 keeps
/// the graph acyclic). The audit row must never drift from the status change it records
/// (DA-023 §P3.8 constraint #6).
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Executes <paramref name="action"/> inside a database transaction and commits it (or
    /// rolls back if the action throws). The action stages its changes on the repositories'
    /// shared tracked context; the single <c>SaveChanges</c> + commit happens here.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct);
}
