using DocuPilot.Models.Enums;

namespace DocuPilot.Models.Entities;

/// <summary>
/// Persistence entity for an AI-recommended / created workflow task (table <c>WorkflowTasks</c>,
/// spec §9.5), the SECOND 1:N child of <see cref="Document"/> (one document → many tasks) and the
/// FIFTH cascading child overall (DBA DA-053 §P8.2). Mapped 1:N like <see cref="DocumentChunk"/>
/// (<c>HasOne&lt;Document&gt;().WithMany().HasForeignKey(t =&gt; t.DocumentId).OnDelete(Cascade)</c>),
/// but with NO uniqueness invariant — a document may legitimately hold many tasks (PM Q7, no
/// <c>UNIQUE</c>). The only AI-reachable write in the system, and only ever through the validated,
/// audited <c>create_workflow_task</c> tool → <c>IWorkflowService.CreateTaskAsync</c>. A plain POCO —
/// all mapping (enum-string Priority/Status, NVARCHAR(MAX) Reason, cascade FK, the two non-unique
/// indexes) is fluent in <c>WorkflowTaskConfiguration</c>.
/// </summary>
public sealed class WorkflowTask
{
    /// <summary>Primary key. App-generated via <c>Guid.CreateVersion7()</c> — no DB-side default (<c>ValueGeneratedNever</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>FK → <c>Documents.Id</c> (ON DELETE CASCADE), non-unique-indexed. Many task rows may share one <c>DocumentId</c> (1:N).</summary>
    public Guid DocumentId { get; set; }

    /// <summary>The recommended workflow / task type, e.g. <c>"LegalReview"</c>, <c>"FinanceApproval"</c>. Free string (<c>NVARCHAR(100)</c>) — the workflow-name set is open-ended.</summary>
    public string TaskType { get; set; } = string.Empty;

    /// <summary>The owning team, e.g. <c>"Legal"</c>, <c>"Finance"</c>. Free string (<c>NVARCHAR(100)</c>).</summary>
    public string AssignedTeam { get; set; } = string.Empty;

    /// <summary>Closed-set priority (<c>NVARCHAR(50)</c> enum-string). Off-list recommendations coerce to <see cref="WorkflowPriority.Normal"/>.</summary>
    public WorkflowPriority Priority { get; set; }

    /// <summary>The recommendation's justification, carried onto the task for the §11.7 list (<c>NVARCHAR(MAX)</c>). Nullable — a manual task need not carry one.</summary>
    public string? Reason { get; set; }

    /// <summary>Closed-set lifecycle (<c>NVARCHAR(50)</c> enum-string). Created <see cref="WorkflowTaskStatus.Open"/>; <c>Complete</c> flips to <see cref="WorkflowTaskStatus.Completed"/>.</summary>
    public WorkflowTaskStatus Status { get; set; }

    /// <summary>Creation timestamp (UTC), set via <c>TimeProvider</c>.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Completion timestamp (UTC), set when the task is completed; <c>null</c> while <see cref="WorkflowTaskStatus.Open"/>.</summary>
    public DateTime? CompletedAt { get; set; }
}
