using DocuPilot.Models.Contracts;
using DocuPilot.Services.Workflow;
using Microsoft.AspNetCore.Mvc;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Phase-8 CONSTRAINED agent endpoint (DA-054, ADR §2). A FIXED, code-orchestrated recommend → create
/// pipeline for one document — NOT an open-ended LLM tool-calling loop. Thin controller — delegates to
/// <see cref="IAgentPipeline"/> and maps the discriminated outcome. Both underlying tool calls
/// (<c>recommend_workflow</c>, <c>create_workflow_task</c>) go through the dispatcher, so both are
/// schema-validated + audited. Fails fast (503) at the recommend step if the LLM is down — no
/// half-created task.
/// </summary>
[ApiController]
[Route("api/agent")]
public sealed class AgentController : ControllerBase
{
    private const int RetryAfterSeconds = 5;

    private readonly IAgentPipeline _pipeline;

    public AgentController(IAgentPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    /// <summary>
    /// Runs the recommend → create pipeline for a document. <c>200</c> with the recommendation + the
    /// created task; <c>404</c> if the document is missing; <c>400</c>/<c>409</c> if a step rejects;
    /// <c>503</c> (with <c>Retry-After</c>) if the LLM is down (the create step never runs).
    /// </summary>
    [HttpPost("recommend-and-create")]
    [ProducesResponseType(typeof(AgentRecommendAndCreateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RecommendAndCreate([FromBody] AgentRecommendAndCreateRequest request, CancellationToken ct)
    {
        if (request is null || request.DocumentId == Guid.Empty)
        {
            return BadRequest(new { error = "documentId must be a valid GUID." });
        }

        var outcome = await _pipeline.RecommendAndCreateAsync(request.DocumentId, ct);
        switch (outcome.Kind)
        {
            case AgentPipelineOutcomeKind.Succeeded:
                var response = new AgentRecommendAndCreateResponse(
                    WorkflowController.MapRecommendation(outcome.Recommendation!),
                    WorkflowController.MapTask(outcome.Task!));
                return Ok(response);

            case AgentPipelineOutcomeKind.DocumentNotFound:
                return NotFound(new { error = outcome.Error });

            case AgentPipelineOutcomeKind.Unavailable:
                Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { error = "The recommendation service is temporarily unavailable. Please try again shortly." });

            default:
                return outcome.IsConflict
                    ? Conflict(new { error = outcome.Error })
                    : BadRequest(new { error = outcome.Error });
        }
    }
}
