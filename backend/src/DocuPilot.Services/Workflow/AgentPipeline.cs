using System.Text.Json;
using DocuPilot.Services.Tools;
using Microsoft.Extensions.Logging;

namespace DocuPilot.Services.Workflow;

/// <summary>
/// The CONSTRAINED agent pipeline (ADR §2). See <see cref="IAgentPipeline"/>. Code-orchestrated, NOT
/// an LLM-planned loop: step 1 dispatches <c>recommend_workflow</c>, step 2 maps the recommendation
/// to <c>create_workflow_task</c> args and dispatches it. BOTH calls go through the
/// <see cref="IToolDispatcher"/>, so both are schema-validated + audited (ToolInvoked/ToolSucceeded/
/// ToolFailed). Fails fast at step 1 — if the LLM is down, the create step never runs, so there is no
/// half-created state.
/// </summary>
public sealed class AgentPipeline : IAgentPipeline
{
    private readonly IToolDispatcher _dispatcher;
    private readonly ILogger<AgentPipeline> _logger;

    public AgentPipeline(IToolDispatcher dispatcher, ILogger<AgentPipeline> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<AgentPipelineOutcome> RecommendAndCreateAsync(Guid documentId, CancellationToken ct)
    {
        // STEP 1 — recommend_workflow (LLM, audited via the dispatcher).
        var recommendArgs = ToArgs(new { documentId });
        var recommendResult = await _dispatcher.InvokeAsync("recommend_workflow", recommendArgs, ct);

        switch (recommendResult.Kind)
        {
            case ToolResultKind.NotFound:
                return AgentPipelineOutcome.DocumentNotFound;
            case ToolResultKind.Unavailable:
                return AgentPipelineOutcome.Unavailable;
            case ToolResultKind.Rejected:
                return AgentPipelineOutcome.Rejected(recommendResult.Error ?? "The recommendation was rejected.");
            case ToolResultKind.Conflict:
                return AgentPipelineOutcome.Rejected(recommendResult.Error ?? "Conflict.", isConflict: true);
        }

        var recommendation = (WorkflowRecommendationModel)recommendResult.Payload!;

        // STEP 2 — map the recommendation → create_workflow_task args, dispatch (validated write, audited).
        // AssignedTeam is derived from the recommendation's workflow (the recommendation JSON has no
        // explicit team); the dispatcher + service re-validate.
        var createArgs = ToArgs(new
        {
            documentId,
            taskType = recommendation.RecommendedWorkflow,
            assignedTeam = DeriveTeam(recommendation.RecommendedWorkflow),
            priority = recommendation.Priority.ToString(),
            reason = recommendation.Reason,
        });

        var createResult = await _dispatcher.InvokeAsync("create_workflow_task", createArgs, ct);

        switch (createResult.Kind)
        {
            case ToolResultKind.Succeeded:
                var task = (WorkflowTaskModel)createResult.Payload!;
                return AgentPipelineOutcome.Succeeded(recommendation, task);
            case ToolResultKind.NotFound:
                return AgentPipelineOutcome.DocumentNotFound;
            case ToolResultKind.Conflict:
                return AgentPipelineOutcome.Rejected(createResult.Error ?? "Conflict.", isConflict: true);
            default:
                _logger.LogWarning("Agent pipeline create step rejected for {DocumentId}: {Error}", documentId, createResult.Error);
                return AgentPipelineOutcome.Rejected(createResult.Error ?? "The create step was rejected.");
        }
    }

    /// <summary>Heuristic team derivation from the workflow name (the recommendation JSON carries no team).</summary>
    private static string DeriveTeam(string recommendedWorkflow)
    {
        var w = recommendedWorkflow.ToLowerInvariant();
        if (w.Contains("legal"))
        {
            return "Legal";
        }

        if (w.Contains("finance") || w.Contains("invoice") || w.Contains("payment") || w.Contains("approval"))
        {
            return "Finance";
        }

        if (w.Contains("compliance"))
        {
            return "Compliance";
        }

        return "Operations";
    }

    private static JsonElement ToArgs(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }
}
