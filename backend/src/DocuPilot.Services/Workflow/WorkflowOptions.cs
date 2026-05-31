namespace DocuPilot.Services.Workflow;

/// <summary>
/// Workflow / tool-layer bounds, bound from the <c>Workflow</c> config section (env keys
/// <c>Workflow__*</c>). API-only (the Worker does NOT do workflow/tools — ADR §9). ALL keys have
/// code defaults, so the feature works with ZERO env changes (DevOps DA-056 is optional, docs-only).
/// </summary>
public sealed class WorkflowOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Workflow";

    /// <summary>The default priority when the LLM omits / emits an off-list value (coercion target). Default <c>Normal</c>.</summary>
    public string DefaultPriority { get; set; } = "Normal";

    /// <summary>Max characters of the document's text head fed to the recommendation prompt. Default 4,000.</summary>
    public int RecommendTextMaxChars { get; set; } = 4_000;

    /// <summary>Whether <c>create_workflow_task</c> allows duplicate tasks (PM Q7 default). Default <c>true</c> (no hard dedupe).</summary>
    public bool AllowDuplicateTasks { get; set; } = true;
}
