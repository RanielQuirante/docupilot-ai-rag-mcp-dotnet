using DocuPilot.Models.Enums;

namespace DocuPilot.Services.Workflow;

/// <summary>
/// Phase-8 workflow business operations (DA-054, ADR §2/§5/§6). The single, validated, audited
/// layer the tools AND the controllers both call — the tools are thin AI-facing wrappers over these
/// SAME operations (no second write path). <see cref="RecommendAsync"/> is a bounded JSON-mode LLM
/// call (the Phase-4 classification posture); <see cref="CreateTaskAsync"/> is the only mutation in
/// the system, persisting the row + an <c>AuditLog</c> in ONE transaction. All return discriminated
/// outcomes the controller/dispatcher map to status codes WITHOUT throwing for expected states.
/// </summary>
public interface IWorkflowService
{
    /// <summary>
    /// Recommends a workflow for a document via a JSON-mode LLM call over its persisted
    /// classification + metadata + a text head. A down/timed-out LLM ⇒
    /// <see cref="RecommendOutcomeKind.Unavailable"/> (→ 503); a missing document ⇒
    /// <see cref="RecommendOutcomeKind.DocumentNotFound"/> (→ 404); a not-yet-classified document ⇒
    /// <see cref="RecommendOutcomeKind.NotClassified"/> (→ 409).
    /// </summary>
    Task<RecommendOutcome> RecommendAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// The safety-critical write (§5.12 steps 5–6): validates the input (document exists; priority in
    /// enum; taskType/assignedTeam non-empty), then persists a <c>WorkflowTasks</c> row (Status=Open)
    /// AND an <c>AuditLog</c> in ONE <see cref="DocuPilot.Repository.Abstractions.IUnitOfWork"/>
    /// transaction. Invalid input ⇒ <see cref="CreateTaskOutcomeKind.Invalid"/> (→ 400) with NOTHING
    /// written; a missing document ⇒ <see cref="CreateTaskOutcomeKind.DocumentNotFound"/> (→ 404).
    /// </summary>
    Task<CreateTaskOutcome> CreateTaskAsync(CreateTaskInput input, CancellationToken ct);

    /// <summary>Lists tasks newest-first, optionally filtered by status and/or document.</summary>
    Task<IReadOnlyList<WorkflowTaskModel>> ListTasksAsync(WorkflowTaskStatus? status, Guid? documentId, CancellationToken ct);

    /// <summary>
    /// Completes a task: flips <c>Open → Completed</c> and sets <c>CompletedAt</c>, with an audit row,
    /// in ONE transaction. A missing task ⇒ <see cref="CompleteTaskOutcomeKind.NotFound"/> (→ 404); an
    /// already-completed task ⇒ <see cref="CompleteTaskOutcomeKind.AlreadyCompleted"/> (→ 409).
    /// </summary>
    Task<CompleteTaskOutcome> CompleteTaskAsync(Guid taskId, CancellationToken ct);
}

/// <summary>Layer-agnostic create input (keeps Services free of the API <c>CreateWorkflowTaskRequest</c> DTO).</summary>
/// <param name="DocumentId">The owning document (must exist).</param>
/// <param name="TaskType">The recommended workflow / task type (required, non-empty).</param>
/// <param name="AssignedTeam">The owning team (required, non-empty).</param>
/// <param name="Priority">The raw priority string (coerced/validated against <see cref="WorkflowPriority"/>).</param>
/// <param name="Reason">Optional justification.</param>
public sealed record CreateTaskInput(Guid DocumentId, string? TaskType, string? AssignedTeam, string? Priority, string? Reason);

/// <summary>A validated workflow recommendation (mapped to the Contracts DTO by the controller).</summary>
public sealed record WorkflowRecommendationModel(string RecommendedWorkflow, string NextStep, WorkflowPriority Priority, string Reason);

/// <summary>A workflow task at the Services layer (mapped to the Contracts <c>WorkflowTaskDto</c>).</summary>
public sealed record WorkflowTaskModel(
    Guid Id,
    Guid DocumentId,
    string TaskType,
    string AssignedTeam,
    WorkflowPriority Priority,
    WorkflowTaskStatus Status,
    string? Reason,
    DateTime CreatedAt,
    DateTime? CompletedAt);

// ---- discriminated outcomes ----

/// <summary>Recommendation outcome kind (drives the controller's status-code mapping).</summary>
public enum RecommendOutcomeKind
{
    /// <summary>A recommendation was produced (→ 200).</summary>
    Recommendation,

    /// <summary>The document does not exist (→ 404).</summary>
    DocumentNotFound,

    /// <summary>The document has no classification yet — recommend would be blind (→ 409).</summary>
    NotClassified,

    /// <summary>The LLM was down/timed-out (→ 503 + Retry-After).</summary>
    Unavailable,
}

/// <summary>The result of <see cref="IWorkflowService.RecommendAsync"/>.</summary>
public sealed class RecommendOutcome
{
    private RecommendOutcome(RecommendOutcomeKind kind, WorkflowRecommendationModel? recommendation)
    {
        Kind = kind;
        Recommendation = recommendation;
    }

    public RecommendOutcomeKind Kind { get; }

    public WorkflowRecommendationModel? Recommendation { get; }

    public static RecommendOutcome FromRecommendation(WorkflowRecommendationModel recommendation) =>
        new(RecommendOutcomeKind.Recommendation, recommendation);

    public static RecommendOutcome DocumentNotFound { get; } = new(RecommendOutcomeKind.DocumentNotFound, null);

    public static RecommendOutcome NotClassified { get; } = new(RecommendOutcomeKind.NotClassified, null);

    public static RecommendOutcome Unavailable { get; } = new(RecommendOutcomeKind.Unavailable, null);
}

/// <summary>Create-task outcome kind (drives the controller's status-code mapping).</summary>
public enum CreateTaskOutcomeKind
{
    /// <summary>The task was created (row + audit) (→ 201).</summary>
    Created,

    /// <summary>The input failed validation (missing/blank required field, off-enum priority) — NOTHING written (→ 400).</summary>
    Invalid,

    /// <summary>The referenced document does not exist — NOTHING written (→ 404).</summary>
    DocumentNotFound,

    /// <summary>(Optional soft guard) a matching Open task already exists (→ 409). Disabled by default (PM Q7).</summary>
    DuplicateOpenTask,
}

/// <summary>The result of <see cref="IWorkflowService.CreateTaskAsync"/>.</summary>
public sealed class CreateTaskOutcome
{
    private CreateTaskOutcome(CreateTaskOutcomeKind kind, WorkflowTaskModel? task, string? error)
    {
        Kind = kind;
        Task = task;
        Error = error;
    }

    public CreateTaskOutcomeKind Kind { get; }

    public WorkflowTaskModel? Task { get; }

    /// <summary>A human-readable validation/error message for the <see cref="CreateTaskOutcomeKind.Invalid"/> / duplicate paths.</summary>
    public string? Error { get; }

    public static CreateTaskOutcome Created(WorkflowTaskModel task) => new(CreateTaskOutcomeKind.Created, task, null);

    public static CreateTaskOutcome Invalid(string error) => new(CreateTaskOutcomeKind.Invalid, null, error);

    public static CreateTaskOutcome DocumentNotFound { get; } = new(CreateTaskOutcomeKind.DocumentNotFound, null, "The referenced document does not exist.");

    public static CreateTaskOutcome DuplicateOpenTask(string error) => new(CreateTaskOutcomeKind.DuplicateOpenTask, null, error);
}

/// <summary>Complete-task outcome kind (drives the controller's status-code mapping).</summary>
public enum CompleteTaskOutcomeKind
{
    /// <summary>The task was completed (→ 200 with the updated DTO).</summary>
    Completed,

    /// <summary>No task with that id (→ 404).</summary>
    NotFound,

    /// <summary>The task was already <c>Completed</c> (→ 409).</summary>
    AlreadyCompleted,
}

/// <summary>The result of <see cref="IWorkflowService.CompleteTaskAsync"/>.</summary>
public sealed class CompleteTaskOutcome
{
    private CompleteTaskOutcome(CompleteTaskOutcomeKind kind, WorkflowTaskModel? task)
    {
        Kind = kind;
        Task = task;
    }

    public CompleteTaskOutcomeKind Kind { get; }

    public WorkflowTaskModel? Task { get; }

    public static CompleteTaskOutcome Completed(WorkflowTaskModel task) => new(CompleteTaskOutcomeKind.Completed, task);

    public static CompleteTaskOutcome NotFound { get; } = new(CompleteTaskOutcomeKind.NotFound, null);

    public static CompleteTaskOutcome AlreadyCompleted { get; } = new(CompleteTaskOutcomeKind.AlreadyCompleted, null);
}
