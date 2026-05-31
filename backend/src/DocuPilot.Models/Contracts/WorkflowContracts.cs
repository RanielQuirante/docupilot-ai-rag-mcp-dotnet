namespace DocuPilot.Models.Contracts;

/// <summary>Request body for <c>POST /api/workflows/recommend</c> (spec §10).</summary>
/// <param name="DocumentId">The document to recommend a workflow for.</param>
public sealed record RecommendWorkflowRequest(Guid DocumentId);

/// <summary>
/// Response for <c>POST /api/workflows/recommend</c> (spec §5.11/§10) — the AI workflow recommendation
/// JSON. <c>Priority</c> is the <c>WorkflowPriority</c> name string (<c>Low</c>|<c>Normal</c>|<c>High</c>).
/// </summary>
public sealed record WorkflowRecommendationResponse(
    string RecommendedWorkflow,
    string NextStep,
    string Priority,
    string Reason);

/// <summary>Request body for <c>POST /api/workflow-tasks</c> (the validated, audited create).</summary>
/// <param name="DocumentId">The owning document (must exist → else 404).</param>
/// <param name="TaskType">The recommended workflow / task type (required).</param>
/// <param name="AssignedTeam">The owning team (required).</param>
/// <param name="Priority"><c>Low</c>|<c>Normal</c>|<c>High</c> (off-value → 400).</param>
/// <param name="Reason">Optional justification.</param>
public sealed record CreateWorkflowTaskRequest(
    Guid DocumentId,
    string? TaskType,
    string? AssignedTeam,
    string? Priority,
    string? Reason);

/// <summary>
/// Wire DTO for a workflow task (the §11.7 list row + the create response). <c>Priority</c>/<c>Status</c>
/// are the enum name strings (<c>Low|Normal|High</c> / <c>Open|Completed</c>).
/// </summary>
public sealed record WorkflowTaskDto(
    Guid Id,
    Guid DocumentId,
    string TaskType,
    string AssignedTeam,
    string Priority,
    string Status,
    string? Reason,
    DateTime CreatedAt,
    DateTime? CompletedAt);

/// <summary>Wire DTO for a registered tool (introspection — <c>GET /api/tools</c>, spec §5.12).</summary>
public sealed record ToolDefinitionDto(string Name, string Description, string InputSchema);

/// <summary>Request body for <c>POST /api/agent/recommend-and-create</c> (the constrained pipeline).</summary>
public sealed record AgentRecommendAndCreateRequest(Guid DocumentId);

/// <summary>Response for <c>POST /api/agent/recommend-and-create</c> — the recommendation + the created task.</summary>
public sealed record AgentRecommendAndCreateResponse(
    WorkflowRecommendationResponse Recommendation,
    WorkflowTaskDto Task);
