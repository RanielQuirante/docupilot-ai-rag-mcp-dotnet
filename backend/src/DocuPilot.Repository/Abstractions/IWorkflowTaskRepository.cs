using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;

namespace DocuPilot.Repository.Abstractions;

/// <summary>
/// Data-access port for the <c>WorkflowTasks</c> table (1:N child of Documents — DBA DA-053). Both
/// the interface and impl live in the Repository project (DA-011 §2.5). Pure data access — no
/// business logic. The create write is STAGE-ONLY (<see cref="AddTrackedAsync"/>) so the orchestrator
/// commits it atomically alongside its <c>AuditLog</c> in one <see cref="IUnitOfWork"/> transaction
/// (the headline safety guarantee — the row + its audit can never drift).
/// </summary>
public interface IWorkflowTaskRepository
{
    /// <summary>
    /// Stages a new task row on the tracked context WITHOUT calling SaveChanges, so the caller can
    /// commit it atomically alongside the <c>ToolSucceeded</c>/create audit row via
    /// <see cref="IUnitOfWork"/>.
    /// </summary>
    Task AddTrackedAsync(WorkflowTask task, CancellationToken ct);

    /// <summary>
    /// Loads a single tracked task by id, or <c>null</c> if it does not exist. Tracked (not
    /// <c>AsNoTracking</c>) so the caller can mutate + persist it inside a transaction (complete).
    /// </summary>
    Task<WorkflowTask?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Lists tasks newest-first (<c>CreatedAt DESC</c>), optionally filtered by <paramref name="status"/>
    /// and/or <paramref name="documentId"/> (each <c>null</c> ⇒ no filter on that dimension).
    /// No-tracking (a pure read for the §11.7 list page).
    /// </summary>
    Task<IReadOnlyList<WorkflowTask>> ListAsync(WorkflowTaskStatus? status, Guid? documentId, CancellationToken ct);

    /// <summary>
    /// Counts <c>Open</c> tasks of a given <paramref name="taskType"/> for a document — the optional
    /// soft duplicate guard (PM Q7; not used by default). No-tracking.
    /// </summary>
    Task<int> CountOpenByDocumentAsync(Guid documentId, string taskType, CancellationToken ct);

    /// <summary>
    /// Dashboard aggregate (Phase 9, DA-058): a single server-side <c>COUNT</c> of tasks in the given
    /// <paramref name="status"/> (the dashboard passes <c>Open</c>). No row materialization; backed by
    /// <c>IX_WorkflowTasks_Status</c>. No-tracking.
    /// </summary>
    Task<int> CountByStatusAsync(WorkflowTaskStatus status, CancellationToken ct);
}
