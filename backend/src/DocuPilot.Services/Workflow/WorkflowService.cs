using System.Text.Json;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuPilot.Services.Workflow;

/// <summary>
/// Phase-8 workflow orchestrator (DA-054). See <see cref="IWorkflowService"/>. The single validated,
/// audited business layer the tools AND the controllers both call. <see cref="RecommendAsync"/> is a
/// bounded JSON-mode LLM call (the Phase-4 <c>ClassificationService</c> pattern — temp 0, prompt-as-
/// resource, defensive parse + coerce); <see cref="CreateTaskAsync"/> is the ONLY mutation in the
/// system and persists the row + an <c>AuditLog</c> in ONE <see cref="IUnitOfWork"/> transaction. All
/// ports are stubbable so the flow is unit-testable with no network.
/// </summary>
public sealed class WorkflowService : IWorkflowService
{
    private const string WorkflowTaskEntityName = "WorkflowTask";
    private const int MaxReasonLength = 1000;
    private const int MaxNameLength = 100;

    private const string RecommendationSystemPrompt =
        "You are an enterprise document workflow assistant. Respond with a single JSON object only.";

    private readonly IDocumentRepository _documents;
    private readonly IDocumentClassificationRepository _classifications;
    private readonly IExtractedMetadataRepository _metadata;
    private readonly IDocumentTextRepository _texts;
    private readonly IWorkflowTaskRepository _tasks;
    private readonly IAuditRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILlmClient _llm;
    private readonly IPromptProvider _prompts;
    private readonly TimeProvider _timeProvider;
    private readonly LlmOptions _llmOptions;
    private readonly WorkflowOptions _options;
    private readonly ILogger<WorkflowService> _logger;

    public WorkflowService(
        IDocumentRepository documents,
        IDocumentClassificationRepository classifications,
        IExtractedMetadataRepository metadata,
        IDocumentTextRepository texts,
        IWorkflowTaskRepository tasks,
        IAuditRepository audit,
        IUnitOfWork unitOfWork,
        ILlmClient llm,
        IPromptProvider prompts,
        TimeProvider timeProvider,
        IOptions<LlmOptions> llmOptions,
        IOptions<WorkflowOptions> options,
        ILogger<WorkflowService> logger)
    {
        _documents = documents;
        _classifications = classifications;
        _metadata = metadata;
        _texts = texts;
        _tasks = tasks;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _llm = llm;
        _prompts = prompts;
        _timeProvider = timeProvider;
        _llmOptions = llmOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RecommendOutcome> RecommendAsync(Guid documentId, CancellationToken ct)
    {
        var document = await _documents.GetByIdAsync(documentId, ct);
        if (document is null)
        {
            return RecommendOutcome.DocumentNotFound;
        }

        // Recommend over what Phase-4 ALREADY computed — never re-classify blind. No classification
        // yet ⇒ NotClassified (→ 409), not a blind recommendation.
        var classification = await _classifications.GetByDocumentIdAsync(documentId, ct);
        if (classification is null)
        {
            return RecommendOutcome.NotClassified;
        }

        var categoryDisplay = DocumentCategoryNames.ToDisplay(classification.Classification);

        var metadataRow = await _metadata.GetByDocumentIdAsync(documentId, ct);
        var metadataJson = string.IsNullOrWhiteSpace(metadataRow?.MetadataJson) ? "{}" : metadataRow!.MetadataJson;

        var textRow = await _texts.GetByDocumentIdAsync(documentId, ct);
        var textHead = Truncate(textRow?.Content ?? string.Empty, Math.Max(1, _options.RecommendTextMaxChars));

        var prompt = _prompts.BuildWorkflowRecommendationPrompt(categoryDisplay, metadataJson, textHead);

        LlmResponse response;
        try
        {
            response = await CallLlmAsync(RecommendationSystemPrompt, prompt, ct);
        }
        catch (LlmUnavailableException ex)
        {
            // Synchronous user-facing path: a down/timed-out LLM is "try again" → 503, NOT a 500-storm
            // and NOT the Worker Transient (there is no backlog to defer to). (ADR §5 / PM Q8.)
            _logger.LogWarning(ex, "Workflow recommendation unavailable: the chat LLM is down/warming/timed-out for {DocumentId}.", documentId);
            return RecommendOutcome.Unavailable;
        }

        // Defensive parse + coerce (the Phase-4 posture): off-value priority → Normal; null fields →
        // safe defaults. A flaky 3B model never hard-fails the endpoint.
        var recommendation = ParseRecommendation(response.Content, categoryDisplay);
        return RecommendOutcome.FromRecommendation(recommendation);
    }

    public async Task<CreateTaskOutcome> CreateTaskAsync(CreateTaskInput input, CancellationToken ct)
    {
        // 1) VALIDATE input (defense in depth — the dispatcher also schema-validates upstream). Any
        // failure ⇒ Invalid (→ 400) with NOTHING written.
        var taskType = input.TaskType?.Trim();
        var assignedTeam = input.AssignedTeam?.Trim();

        if (string.IsNullOrWhiteSpace(taskType))
        {
            return CreateTaskOutcome.Invalid("taskType is required.");
        }

        if (string.IsNullOrWhiteSpace(assignedTeam))
        {
            return CreateTaskOutcome.Invalid("assignedTeam is required.");
        }

        if (!WorkflowPriorityNames.IsKnown(input.Priority))
        {
            return CreateTaskOutcome.Invalid(
                $"priority must be one of: {string.Join(", ", WorkflowPriorityNames.Names)}.");
        }

        // Length-cap the free strings to the column bounds (NVARCHAR(100)).
        taskType = Truncate(taskType, MaxNameLength);
        assignedTeam = Truncate(assignedTeam, MaxNameLength);
        var priority = WorkflowPriorityNames.Coerce(input.Priority);
        var reason = Truncate(input.Reason, MaxReasonLength);

        // 2) The document must EXIST (→ 404 / nothing written).
        var document = await _documents.GetByIdAsync(input.DocumentId, ct);
        if (document is null)
        {
            return CreateTaskOutcome.DocumentNotFound;
        }

        // 3) Optional soft duplicate guard (disabled by default — PM Q7 allows duplicates, audited).
        if (!_options.AllowDuplicateTasks)
        {
            var openCount = await _tasks.CountOpenByDocumentAsync(input.DocumentId, taskType, ct);
            if (openCount > 0)
            {
                return CreateTaskOutcome.DuplicateOpenTask(
                    $"An open '{taskType}' task already exists for this document.");
            }
        }

        // 4) PERSIST the row (Status=Open) AND its audit in ONE transaction — they can never drift.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var task = new WorkflowTask
        {
            Id = Guid.CreateVersion7(),
            DocumentId = input.DocumentId,
            TaskType = taskType,
            AssignedTeam = assignedTeam,
            Priority = priority,
            Reason = reason,
            Status = WorkflowTaskStatus.Open,
            CreatedAt = now,
            CompletedAt = null,
        };

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            await _tasks.AddTrackedAsync(task, innerCt);

            await _audit.AddAsync(BuildAudit(task.Id, AuditAction.ToolSucceeded, now,
                JsonSerializer.Serialize(new
                {
                    tool = "create_workflow_task",
                    documentId = task.DocumentId,
                    taskId = task.Id,
                    taskType = task.TaskType,
                    assignedTeam = task.AssignedTeam,
                    priority = task.Priority.ToString(),
                    status = task.Status.ToString(),
                })), innerCt);
        }, ct);

        return CreateTaskOutcome.Created(ToModel(task));
    }

    public async Task<IReadOnlyList<WorkflowTaskModel>> ListTasksAsync(WorkflowTaskStatus? status, Guid? documentId, CancellationToken ct)
    {
        var rows = await _tasks.ListAsync(status, documentId, ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<CompleteTaskOutcome> CompleteTaskAsync(Guid taskId, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(taskId, ct);
        if (task is null)
        {
            return CompleteTaskOutcome.NotFound;
        }

        if (task.Status == WorkflowTaskStatus.Completed)
        {
            return CompleteTaskOutcome.AlreadyCompleted;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            task.Status = WorkflowTaskStatus.Completed;
            task.CompletedAt = now;

            await _audit.AddAsync(BuildAudit(task.Id, AuditAction.ToolSucceeded, now,
                JsonSerializer.Serialize(new
                {
                    tool = "complete_workflow_task",
                    taskId = task.Id,
                    documentId = task.DocumentId,
                    status = task.Status.ToString(),
                })), innerCt);
        }, ct);

        return CompleteTaskOutcome.Completed(ToModel(task));
    }

    // ---- LLM call (per-call timeout linked to the request token, Phase-4/6/7 posture) ----

    private async Task<LlmResponse> CallLlmAsync(string system, string prompt, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_llmOptions.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await _llm.CompleteAsync(
                new LlmRequest(prompt, system, JsonMode: true, Temperature: 0),
                linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation — propagate
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new LlmUnavailableException($"Workflow recommendation LLM call timed out after {_llmOptions.TimeoutSeconds}s.");
        }
    }

    // ---- parsing / coercion (mirrors ClassificationService — never hard-fails on a flaky model) ----

    private WorkflowRecommendationModel ParseRecommendation(string content, string categoryDisplay)
    {
        var json = ExtractJsonObject(content);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var recommendedWorkflow = ReadString(root, "recommendedWorkflow");
                    var nextStep = ReadString(root, "nextStep");
                    var priorityRaw = ReadString(root, "priority");
                    var reason = ReadString(root, "reason");

                    var priority = WorkflowPriorityNames.Coerce(priorityRaw);

                    return new WorkflowRecommendationModel(
                        Truncate(string.IsNullOrWhiteSpace(recommendedWorkflow) ? "Manual Review" : recommendedWorkflow!.Trim(), MaxNameLength),
                        Truncate(string.IsNullOrWhiteSpace(nextStep) ? "Review the document manually." : nextStep!.Trim(), MaxReasonLength),
                        priority,
                        Truncate(string.IsNullOrWhiteSpace(reason) ? $"Recommendation for a {categoryDisplay} document." : reason!.Trim(), MaxReasonLength));
                }
            }
            catch (JsonException)
            {
                // fall through to the safe default
            }
        }

        // Unparseable model output → a safe default recommendation so the endpoint never hard-fails.
        _logger.LogWarning("Workflow recommendation returned unparseable JSON; returning a safe default for a {Category} document.", categoryDisplay);
        return new WorkflowRecommendationModel(
            "Manual Review",
            "Review the document manually.",
            WorkflowPriority.Normal,
            "Could not derive a confident recommendation.");
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>Pulls the first balanced top-level JSON object out of a model response (tolerant of prose/fences).</summary>
    private static string? ExtractJsonObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return content[start..(end + 1)];
    }

    // ---- helpers ----

    private static WorkflowTaskModel ToModel(WorkflowTask t) => new(
        t.Id,
        t.DocumentId,
        t.TaskType,
        t.AssignedTeam,
        t.Priority,
        t.Status,
        t.Reason,
        t.CreatedAt,
        t.CompletedAt);

    private static AuditLog BuildAudit(Guid entityId, AuditAction action, DateTime createdAt, string? detailsJson) => new()
    {
        Id = Guid.CreateVersion7(),
        EntityName = WorkflowTaskEntityName,
        EntityId = entityId,
        Action = action.ToString(),
        DetailsJson = detailsJson,
        CreatedAt = createdAt,
    };

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int max) =>
        value is not null && value.Length > max ? value[..max] : value;
}
