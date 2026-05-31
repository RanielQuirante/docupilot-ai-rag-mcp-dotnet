namespace DocuPilot.Services.Workflow;

/// <summary>
/// The CONSTRAINED agent pipeline (ADR §2 — NOT an open-ended LLM tool-calling loop). A fixed,
/// code-orchestrated two-step backbone for a single document: dispatch <c>recommend_workflow</c>
/// (LLM, audited) → map its output to <c>create_workflow_task</c> args → dispatch
/// <c>create_workflow_task</c> (validated write, audited). The sequence is hard-coded in C#; the
/// model only fills the recommendation JSON. Both underlying tool calls go through the
/// <c>IToolDispatcher</c>, so both are schema-validated + audited. Fails fast at step 1 if the LLM is
/// down (no half-created state — the create step never runs).
/// </summary>
public interface IAgentPipeline
{
    /// <summary>Runs recommend → create for one document and returns a discriminated outcome.</summary>
    Task<AgentPipelineOutcome> RecommendAndCreateAsync(Guid documentId, CancellationToken ct);
}

/// <summary>Agent-pipeline outcome kind (drives the controller's status-code mapping).</summary>
public enum AgentPipelineOutcomeKind
{
    /// <summary>Both steps succeeded (→ 200 with the recommendation + the created task).</summary>
    Succeeded,

    /// <summary>The document does not exist (→ 404).</summary>
    DocumentNotFound,

    /// <summary>The document is not yet classified, or a step rejected the args (→ 400/409 via <see cref="AgentPipelineOutcome.IsConflict"/>).</summary>
    Rejected,

    /// <summary>The LLM was down on the recommend step (→ 503). The create step never ran.</summary>
    Unavailable,
}

/// <summary>The result of <see cref="IAgentPipeline.RecommendAndCreateAsync"/>.</summary>
public sealed class AgentPipelineOutcome
{
    private AgentPipelineOutcome(
        AgentPipelineOutcomeKind kind,
        WorkflowRecommendationModel? recommendation,
        WorkflowTaskModel? task,
        string? error,
        bool isConflict)
    {
        Kind = kind;
        Recommendation = recommendation;
        Task = task;
        Error = error;
        IsConflict = isConflict;
    }

    public AgentPipelineOutcomeKind Kind { get; }

    public WorkflowRecommendationModel? Recommendation { get; }

    public WorkflowTaskModel? Task { get; }

    public string? Error { get; }

    /// <summary>When <see cref="Kind"/> is <see cref="AgentPipelineOutcomeKind.Rejected"/>, distinguishes a 409 (conflict) from a 400.</summary>
    public bool IsConflict { get; }

    public static AgentPipelineOutcome Succeeded(WorkflowRecommendationModel recommendation, WorkflowTaskModel task) =>
        new(AgentPipelineOutcomeKind.Succeeded, recommendation, task, null, false);

    public static AgentPipelineOutcome DocumentNotFound { get; } =
        new(AgentPipelineOutcomeKind.DocumentNotFound, null, null, "The referenced document does not exist.", false);

    public static AgentPipelineOutcome Rejected(string error, bool isConflict = false) =>
        new(AgentPipelineOutcomeKind.Rejected, null, null, error, isConflict);

    public static AgentPipelineOutcome Unavailable { get; } =
        new(AgentPipelineOutcomeKind.Unavailable, null, null, "The recommendation service is temporarily unavailable.", false);
}
