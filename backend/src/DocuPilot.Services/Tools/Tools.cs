using System.Text.Json;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Search;
using DocuPilot.Services.Workflow;

namespace DocuPilot.Services.Tools;

// The focused Phase-8 tool set (ADR §4). Each tool is a thin AI-facing wrapper that delegates to an
// already-validated business operation (IWorkflowService / ISearchService / a repo) — NO tool
// re-implements logic or touches the DB raw. Args arrive ALREADY schema-validated by the dispatcher;
// each handler returns a ToolResult (the dispatcher audits every call). Small, dependency-light
// argument readers keep them tidy.

internal static class ToolArgs
{
    public static Guid GetGuid(JsonElement args, string name) =>
        Guid.Parse(args.GetProperty(name).GetString()!);

    public static string GetString(JsonElement args, string name) =>
        args.GetProperty(name).GetString()!;

    public static string? GetOptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    public static int? GetOptionalInt(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(el.GetString(), out var n) => n,
            _ => null,
        };
    }
}

/// <summary><c>search_documents</c> — wraps the Phase-6 <see cref="ISearchService"/> verbatim (read).</summary>
public sealed class SearchDocumentsTool : ITool
{
    private readonly ISearchService _search;

    public SearchDocumentsTool(ISearchService search) => _search = search;

    public string Name => "search_documents";

    public string Description => "Semantic search over the uploaded documents. Returns ranked document-level matches.";

    public ToolInputSchema Schema { get; } = new(
        new ToolField("query", ToolFieldType.String, Required: true),
        new ToolField("category", ToolFieldType.String, Required: false),
        new ToolField("limit", ToolFieldType.Integer, Required: false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var query = ToolArgs.GetString(args, "query");
        var category = ToolArgs.GetOptionalString(args, "category");
        var limit = ToolArgs.GetOptionalInt(args, "limit");

        var outcome = await _search.SearchAsync(new SearchQuery(query, limit, category), ct);
        return outcome.Kind switch
        {
            SearchOutcomeKind.EmptyQuery => ToolResult.Rejected("query must not be empty."),
            SearchOutcomeKind.Unavailable => ToolResult.Unavailable("Search is temporarily unavailable."),
            _ => ToolResult.Succeeded(outcome.Results),
        };
    }
}

/// <summary><c>get_document_by_id</c> — loads a document (+ its classification display) by id (read).</summary>
public sealed class GetDocumentByIdTool : ITool
{
    private readonly IDocumentRepository _documents;
    private readonly IDocumentClassificationRepository _classifications;

    public GetDocumentByIdTool(IDocumentRepository documents, IDocumentClassificationRepository classifications)
    {
        _documents = documents;
        _classifications = classifications;
    }

    public string Name => "get_document_by_id";

    public string Description => "Fetch a single document's metadata (filename, status, classification) by its id.";

    public ToolInputSchema Schema { get; } = new(
        new ToolField("documentId", ToolFieldType.Guid, Required: true));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var documentId = ToolArgs.GetGuid(args, "documentId");
        var document = await _documents.GetByIdAsync(documentId, ct);
        if (document is null)
        {
            return ToolResult.NotFound($"Document '{documentId}' was not found.");
        }

        var classification = await _classifications.GetByDocumentIdAsync(documentId, ct);
        return ToolResult.Succeeded(new
        {
            id = document.Id,
            fileName = document.FileName,
            status = document.Status.ToString(),
            classification = classification is null ? null : DocumentCategoryNames.ToDisplay(classification.Classification),
            uploadedAt = document.UploadedAt,
        });
    }
}

/// <summary><c>get_pending_documents</c> — documents awaiting processing (non-terminal status) (read).</summary>
public sealed class GetPendingDocumentsTool : ITool
{
    // "Pending" = uploaded/queued/in-flight — NOT yet ReadyForSearch and NOT Failed (ADR §4).
    private static readonly HashSet<DocumentStatus> PendingStatuses =
    [
        DocumentStatus.Uploaded,
        DocumentStatus.Queued,
        DocumentStatus.ExtractingText,
        DocumentStatus.TextExtracted,
        DocumentStatus.Classifying,
        DocumentStatus.Classified,
        DocumentStatus.GeneratingEmbeddings,
    ];

    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly IDocumentRepository _documents;

    public GetPendingDocumentsTool(IDocumentRepository documents) => _documents = documents;

    public string Name => "get_pending_documents";

    public string Description => "List documents still awaiting processing (not yet ready for search, not failed).";

    public ToolInputSchema Schema { get; } = new(
        new ToolField("limit", ToolFieldType.Integer, Required: false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var limit = ToolArgs.GetOptionalInt(args, "limit") is { } l && l > 0 ? Math.Min(l, MaxLimit) : DefaultLimit;

        // Scan the newest page (POC scale) and filter to the pending statuses, capped to `limit`.
        var (items, _) = await _documents.ListAsync(page: 1, pageSize: MaxLimit, ct);
        var pending = items
            .Where(d => PendingStatuses.Contains(d.Status))
            .Take(limit)
            .Select(d => new
            {
                id = d.Id,
                fileName = d.FileName,
                status = d.Status.ToString(),
                uploadedAt = d.UploadedAt,
            })
            .ToList();

        return ToolResult.Succeeded(pending);
    }
}

/// <summary><c>extract_metadata</c> — reads the persisted <c>ExtractedMetadata</c> JSON for a document (read).</summary>
public sealed class ExtractMetadataTool : ITool
{
    private readonly IDocumentRepository _documents;
    private readonly IExtractedMetadataRepository _metadata;

    public ExtractMetadataTool(IDocumentRepository documents, IExtractedMetadataRepository metadata)
    {
        _documents = documents;
        _metadata = metadata;
    }

    public string Name => "extract_metadata";

    public string Description => "Return the structured metadata previously extracted for a document.";

    public ToolInputSchema Schema { get; } = new(
        new ToolField("documentId", ToolFieldType.Guid, Required: true));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var documentId = ToolArgs.GetGuid(args, "documentId");
        var document = await _documents.GetByIdAsync(documentId, ct);
        if (document is null)
        {
            return ToolResult.NotFound($"Document '{documentId}' was not found.");
        }

        var metadata = await _metadata.GetByDocumentIdAsync(documentId, ct);
        return ToolResult.Succeeded(new
        {
            documentId,
            metadataJson = metadata?.MetadataJson ?? "{}",
        });
    }
}

/// <summary><c>recommend_workflow</c> — the bounded LLM call via <see cref="IWorkflowService.RecommendAsync"/>.</summary>
public sealed class RecommendWorkflowTool : ITool
{
    private readonly IWorkflowService _workflow;

    public RecommendWorkflowTool(IWorkflowService workflow) => _workflow = workflow;

    public string Name => "recommend_workflow";

    public string Description => "Recommend a workflow (recommendedWorkflow, nextStep, priority, reason) for a classified document.";

    public ToolInputSchema Schema { get; } = new(
        new ToolField("documentId", ToolFieldType.Guid, Required: true));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var documentId = ToolArgs.GetGuid(args, "documentId");
        var outcome = await _workflow.RecommendAsync(documentId, ct);
        return outcome.Kind switch
        {
            RecommendOutcomeKind.DocumentNotFound => ToolResult.NotFound($"Document '{documentId}' was not found."),
            RecommendOutcomeKind.NotClassified => ToolResult.Rejected("Document has not been classified yet."),
            RecommendOutcomeKind.Unavailable => ToolResult.Unavailable("The recommendation service is temporarily unavailable."),
            _ => ToolResult.Succeeded(outcome.Recommendation!),
        };
    }
}

/// <summary><c>create_workflow_task</c> — THE safety-critical write via <see cref="IWorkflowService.CreateTaskAsync"/>.</summary>
public sealed class CreateWorkflowTaskTool : ITool
{
    private readonly IWorkflowService _workflow;

    public CreateWorkflowTaskTool(IWorkflowService workflow) => _workflow = workflow;

    public string Name => "create_workflow_task";

    public string Description => "Create a workflow task for a document (the validated, audited write). Persists a task row + an audit log.";

    public ToolInputSchema Schema { get; } = new(
        new ToolField("documentId", ToolFieldType.Guid, Required: true),
        new ToolField("taskType", ToolFieldType.String, Required: true),
        new ToolField("assignedTeam", ToolFieldType.String, Required: true),
        new ToolField("priority", ToolFieldType.Enum, Required: true, WorkflowPriorityNames.Names),
        new ToolField("reason", ToolFieldType.String, Required: false));

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var input = new CreateTaskInput(
            ToolArgs.GetGuid(args, "documentId"),
            ToolArgs.GetString(args, "taskType"),
            ToolArgs.GetString(args, "assignedTeam"),
            ToolArgs.GetString(args, "priority"),
            ToolArgs.GetOptionalString(args, "reason"));

        var outcome = await _workflow.CreateTaskAsync(input, ct);
        return outcome.Kind switch
        {
            CreateTaskOutcomeKind.Invalid => ToolResult.Rejected(outcome.Error!),
            CreateTaskOutcomeKind.DocumentNotFound => ToolResult.NotFound(outcome.Error!),
            CreateTaskOutcomeKind.DuplicateOpenTask => ToolResult.Conflict(outcome.Error!),
            _ => ToolResult.Succeeded(outcome.Task!),
        };
    }
}
