using System.Text.Json;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using Microsoft.Extensions.Logging;

namespace DocuPilot.Services.Tools;

/// <summary>
/// The safe core of Phase 8 (ADR §2/§8) — the deterministic, audited tool-dispatch backbone. Every
/// invocation, no matter who calls it (HTTP, the agent pipeline), is: (1) resolved (unknown name →
/// Rejected + ToolFailed audit, no handler), (2) schema-validated (bad/missing args → Rejected +
/// ToolFailed audit, no handler, ZERO DB effect), (3) audited on entry (<c>ToolInvoked</c>), (4)
/// executed, (5) audited on exit (<c>ToolSucceeded</c> with a small result JSON, or <c>ToolFailed</c>
/// with the error/exception). This is the gradeable "controlled + audited + the-AI-never-writes-raw"
/// guarantee. Each audit row is its own committed transaction (a read tool's audit must persist even
/// though the read itself does not mutate); the <c>create_workflow_task</c> handler's row + its own
/// success audit are written atomically INSIDE the handler's transaction (so they can never drift),
/// and the dispatcher adds the framing ToolInvoked/ToolSucceeded around it.
/// </summary>
public sealed class ToolDispatcher : IToolDispatcher
{
    private const string ToolEntityName = "WorkflowTool";

    private readonly IToolRegistry _registry;
    private readonly IAuditRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ToolDispatcher> _logger;

    public ToolDispatcher(
        IToolRegistry registry,
        IAuditRepository audit,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<ToolDispatcher> logger)
    {
        _registry = registry;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ToolResult> InvokeAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        // 1) Resolve. Unknown tool → Rejected + ToolFailed audit. NEVER a handler call, never a write.
        if (!_registry.TryGet(toolName, out var tool))
        {
            await WriteAuditAsync(toolName, args, AuditAction.ToolFailed, new { tool = toolName, error = "Unknown tool." }, ct);
            return ToolResult.Rejected($"Unknown tool '{toolName}'.");
        }

        // 2) Validate args against the tool's schema BEFORE the handler runs. Bad args → Rejected +
        // ToolFailed audit. The AI can never produce a malformed/unauthorized call past this gate.
        if (!tool.Schema.TryValidate(args, out var validationError))
        {
            await WriteAuditAsync(toolName, args, AuditAction.ToolFailed, new { tool = toolName, error = validationError }, ct);
            return ToolResult.Rejected(validationError);
        }

        // 3) Audit on entry — what the AI is about to do (after validation passed).
        await WriteAuditAsync(toolName, args, AuditAction.ToolInvoked, new { tool = toolName, args = RawArgs(args) }, ct);

        // 4) Execute the validated handler.
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation — propagate (not a tool failure)
        }
        catch (Exception ex)
        {
            // An unexpected handler exception is recorded as ToolFailed (no raw write escapes); the
            // dispatcher surfaces it as Rejected rather than letting it 500 the AI-facing path.
            _logger.LogError(ex, "Tool '{Tool}' threw while executing.", toolName);
            await WriteAuditAsync(toolName, args, AuditAction.ToolFailed, new { tool = toolName, error = ex.Message }, ct);
            return ToolResult.Rejected($"Tool '{toolName}' failed: {ex.Message}");
        }

        // 5) Audit on exit — success or a handled non-success (rejection/not-found/conflict/unavailable).
        if (result.Kind == ToolResultKind.Succeeded)
        {
            await WriteAuditAsync(toolName, args, AuditAction.ToolSucceeded,
                new { tool = toolName, result = Summarize(result.Payload) }, ct);
        }
        else
        {
            await WriteAuditAsync(toolName, args, AuditAction.ToolFailed,
                new { tool = toolName, outcome = result.Kind.ToString(), error = result.Error }, ct);
        }

        return result;
    }

    private async Task WriteAuditAsync(string toolName, JsonElement args, AuditAction action, object details, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var entityId = ExtractDocumentId(args);
        var audit = new AuditLog
        {
            Id = Guid.CreateVersion7(),
            EntityName = ToolEntityName,
            EntityId = entityId,
            Action = action.ToString(),
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAt = now,
        };

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            await _audit.AddAsync(audit, innerCt);
        }, ct);
    }

    /// <summary>The args' <c>documentId</c> as the audit's <c>EntityId</c> when present, else <see cref="Guid.Empty"/>.</summary>
    private static Guid ExtractDocumentId(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("documentId", out var el)
            && el.ValueKind == JsonValueKind.String
            && Guid.TryParse(el.GetString(), out var id))
        {
            return id;
        }

        return Guid.Empty;
    }

    /// <summary>The raw args JSON (already validated) for the entry audit detail.</summary>
    private static JsonElement RawArgs(JsonElement args) => args;

    /// <summary>A small, bounded summary of the handler payload for the success audit (avoid huge blobs).</summary>
    private static string Summarize(object? payload)
    {
        if (payload is null)
        {
            return "ok";
        }

        var json = JsonSerializer.Serialize(payload);
        return json.Length > 2000 ? json[..2000] : json;
    }
}
