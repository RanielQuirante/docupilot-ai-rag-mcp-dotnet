using DocuPilot.Models.Contracts;
using DocuPilot.Models.Enums;
using DocuPilot.Services.Tools;
using DocuPilot.Services.Workflow;
using Microsoft.AspNetCore.Mvc;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Phase-8 workflow endpoints (DA-054). Thin controllers — bind → adapt → delegate (to
/// <see cref="IToolDispatcher"/> / <see cref="IWorkflowService"/>) → map a discriminated outcome to a
/// status code (200/201/400/404/409/503). No business logic. The recommend + create endpoints route
/// THROUGH the tool dispatcher so every AI-style action is schema-validated + audited (the §5.12
/// safety story); list + complete call the service directly (operator actions on the list page).
/// </summary>
[ApiController]
public sealed class WorkflowController : ControllerBase
{
    private const int RetryAfterSeconds = 5;

    private readonly IWorkflowService _workflow;
    private readonly IToolDispatcher _dispatcher;

    public WorkflowController(IWorkflowService workflow, IToolDispatcher dispatcher)
    {
        _workflow = workflow;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Recommends a workflow for a document (spec §10). <c>200</c> with the recommendation;
    /// <c>404</c> if the document is missing; <c>409</c> if it is not yet classified; <c>503</c>
    /// (with <c>Retry-After</c>) if the LLM is temporarily unavailable. Audited as a
    /// <c>recommend_workflow</c> tool call (PM Q9).
    /// </summary>
    [HttpPost("api/workflows/recommend")]
    [ProducesResponseType(typeof(WorkflowRecommendationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Recommend([FromBody] RecommendWorkflowRequest request, CancellationToken ct)
    {
        if (request is null || request.DocumentId == Guid.Empty)
        {
            return BadRequest(new { error = "documentId must be a valid GUID." });
        }

        var result = await _dispatcher.InvokeAsync(
            "recommend_workflow",
            ToolJson.Args(new { documentId = request.DocumentId }),
            ct);

        return result.Kind switch
        {
            ToolResultKind.Succeeded => Ok(MapRecommendation((WorkflowRecommendationModel)result.Payload!)),
            ToolResultKind.NotFound => NotFound(new { error = result.Error }),
            ToolResultKind.Conflict => Conflict(new { error = result.Error }),
            ToolResultKind.Unavailable => Unavailable(),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>
    /// Creates a workflow task (the validated, audited write). <c>201</c> with the created task;
    /// <c>400</c> on invalid args; <c>404</c> if the document is missing; <c>409</c> if a duplicate
    /// soft-guard rejects it (disabled by default). Routes through <c>create_workflow_task</c>.
    /// </summary>
    [HttpPost("api/workflow-tasks")]
    [ProducesResponseType(typeof(WorkflowTaskDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTask([FromBody] CreateWorkflowTaskRequest request, CancellationToken ct)
    {
        if (request is null || request.DocumentId == Guid.Empty)
        {
            return BadRequest(new { error = "documentId must be a valid GUID." });
        }

        var result = await _dispatcher.InvokeAsync(
            "create_workflow_task",
            ToolJson.Args(new
            {
                documentId = request.DocumentId,
                taskType = request.TaskType,
                assignedTeam = request.AssignedTeam,
                priority = request.Priority,
                reason = request.Reason,
            }),
            ct);

        return result.Kind switch
        {
            ToolResultKind.Succeeded => Created(MapTask((WorkflowTaskModel)result.Payload!)),
            ToolResultKind.NotFound => NotFound(new { error = result.Error }),
            ToolResultKind.Conflict => Conflict(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>
    /// Lists workflow tasks newest-first (the §11.7 list), optionally filtered by
    /// <paramref name="status"/> (<c>Open</c>|<c>Completed</c>) and/or <paramref name="documentId"/>.
    /// An unrecognized status ⇒ <c>400</c>.
    /// </summary>
    [HttpGet("api/workflow-tasks")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkflowTaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListTasks([FromQuery] string? status, [FromQuery] Guid? documentId, CancellationToken ct)
    {
        WorkflowTaskStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WorkflowTaskStatus>(status.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return BadRequest(new { error = "status must be 'Open' or 'Completed'." });
            }

            statusFilter = parsed;
        }

        var docFilter = documentId is { } d && d != Guid.Empty ? documentId : null;

        var tasks = await _workflow.ListTasksAsync(statusFilter, docFilter, ct);
        return Ok(tasks.Select(MapTask).ToList());
    }

    /// <summary>
    /// Completes a task: <c>200</c> with the updated DTO (<c>Status=Completed</c>, <c>CompletedAt</c>
    /// set); <c>404</c> if missing; <c>409</c> if already completed.
    /// </summary>
    [HttpPost("api/workflow-tasks/{id:guid}/complete")]
    [ProducesResponseType(typeof(WorkflowTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CompleteTask(Guid id, CancellationToken ct)
    {
        var outcome = await _workflow.CompleteTaskAsync(id, ct);
        return outcome.Kind switch
        {
            CompleteTaskOutcomeKind.Completed => Ok(MapTask(outcome.Task!)),
            CompleteTaskOutcomeKind.AlreadyCompleted => Conflict(new { error = "The task is already completed." }),
            _ => NotFound(new { error = "The task was not found." }),
        };
    }

    private IActionResult Unavailable()
    {
        Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new { error = "The recommendation service is temporarily unavailable. Please try again shortly." });
    }

    private IActionResult Created(WorkflowTaskDto dto) =>
        StatusCode(StatusCodes.Status201Created, dto);

    internal static WorkflowRecommendationResponse MapRecommendation(WorkflowRecommendationModel m) =>
        new(m.RecommendedWorkflow, m.NextStep, m.Priority.ToString(), m.Reason);

    internal static WorkflowTaskDto MapTask(WorkflowTaskModel t) =>
        new(t.Id, t.DocumentId, t.TaskType, t.AssignedTeam, t.Priority.ToString(), t.Status.ToString(), t.Reason, t.CreatedAt, t.CompletedAt);
}
