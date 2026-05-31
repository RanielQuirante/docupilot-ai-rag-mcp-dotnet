using System.Text.Json;

namespace DocuPilot.Services.Tools;

/// <summary>
/// The introspection/wire shape of a registered tool (spec §5.12 concept <c>{name, description,
/// inputSchema}</c>). Returned by <c>GET /api/tools</c>.
/// </summary>
/// <param name="Name">The tool's catalogue name, e.g. <c>"create_workflow_task"</c>.</param>
/// <param name="Description">Human/AI-readable purpose.</param>
/// <param name="InputSchema">A small JSON-schema-ish descriptor of the validated args.</param>
public sealed record ToolDefinition(string Name, string Description, string InputSchema);

/// <summary>The discriminated kind of a <see cref="ToolResult"/>.</summary>
public enum ToolResultKind
{
    /// <summary>The handler ran and succeeded (→ the typed payload). Maps to 200/201.</summary>
    Succeeded,

    /// <summary>The args were rejected (unknown tool, schema-invalid args, or a domain validation failure). Maps to 400.</summary>
    Rejected,

    /// <summary>The document/task the args referenced does not exist. Maps to 404.</summary>
    NotFound,

    /// <summary>A conflicting state (e.g. already-completed, or a duplicate-open-task soft guard). Maps to 409.</summary>
    Conflict,

    /// <summary>An external dependency (the LLM) was unavailable on the synchronous path. Maps to 503.</summary>
    Unavailable,
}

/// <summary>
/// The discriminated outcome of a tool invocation (Phase-8 ADR §2). The dispatcher returns this and
/// audits every call; the controller maps <see cref="Kind"/> to a status code WITHOUT throwing for
/// expected states. <see cref="Payload"/> carries the typed handler result on success (the controller
/// shapes it into the right DTO).
/// </summary>
public sealed class ToolResult
{
    private ToolResult(ToolResultKind kind, object? payload, string? error)
    {
        Kind = kind;
        Payload = payload;
        Error = error;
    }

    public ToolResultKind Kind { get; }

    /// <summary>The typed handler result on <see cref="ToolResultKind.Succeeded"/>; otherwise <c>null</c>.</summary>
    public object? Payload { get; }

    /// <summary>A human-readable error/validation message on the non-success kinds; otherwise <c>null</c>.</summary>
    public string? Error { get; }

    public static ToolResult Succeeded(object payload) => new(ToolResultKind.Succeeded, payload, null);

    public static ToolResult Rejected(string error) => new(ToolResultKind.Rejected, null, error);

    public static ToolResult NotFound(string error) => new(ToolResultKind.NotFound, null, error);

    public static ToolResult Conflict(string error) => new(ToolResultKind.Conflict, null, error);

    public static ToolResult Unavailable(string error) => new(ToolResultKind.Unavailable, null, error);
}

/// <summary>
/// A controlled, AI-facing tool (spec §5.12). Each tool exposes a name + description + a JSON
/// <see cref="InputSchema"/> the dispatcher validates args against, and a handler that delegates to an
/// already-validated business operation (<c>IWorkflowService</c> / <c>ISearchService</c> / a repo) —
/// NO tool re-implements logic or touches the DB raw. The handler receives args that have ALREADY
/// passed schema validation; it returns a <see cref="ToolResult"/> (it does NOT audit — the dispatcher
/// audits every call).
/// </summary>
public interface ITool
{
    /// <summary>The catalogue name (the §5.12 tool name, e.g. <c>"search_documents"</c>).</summary>
    string Name { get; }

    /// <summary>Human/AI-readable purpose.</summary>
    string Description { get; }

    /// <summary>The JSON input-schema descriptor (used for validation + introspection).</summary>
    ToolInputSchema Schema { get; }

    /// <summary>Executes the validated handler. <paramref name="args"/> has already passed <see cref="Schema"/> validation.</summary>
    Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct);
}

/// <summary>
/// The catalogue of registered tools. Composed once in DI from every <see cref="ITool"/> registration.
/// </summary>
public interface IToolRegistry
{
    /// <summary>The tool definitions (<c>{name, description, inputSchema}</c>) for <c>GET /api/tools</c>.</summary>
    IReadOnlyList<ToolDefinition> List();

    /// <summary>Resolves a tool by name for dispatch; <c>false</c> if no tool is registered under <paramref name="name"/>.</summary>
    bool TryGet(string name, out ITool tool);
}

/// <summary>
/// The safe core (Phase-8 ADR §2/§8): resolve the tool → validate args against its schema → write a
/// <c>ToolInvoked</c> audit → execute the handler → write a <c>ToolSucceeded</c> / <c>ToolFailed</c>
/// audit. EVERY invocation is audited; schema-invalid args are <see cref="ToolResultKind.Rejected"/>
/// (→ 400) with a <c>ToolFailed</c> audit and ZERO DB effect, BEFORE the handler ever runs. The AI can
/// never produce a malformed or unauthorized write.
/// </summary>
public interface IToolDispatcher
{
    /// <summary>Validates + audits + executes a tool by name. Unknown tool / bad args ⇒ <see cref="ToolResultKind.Rejected"/> + a <c>ToolFailed</c> audit, no handler call.</summary>
    Task<ToolResult> InvokeAsync(string toolName, JsonElement args, CancellationToken ct);
}
