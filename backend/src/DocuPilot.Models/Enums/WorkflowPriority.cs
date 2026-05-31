namespace DocuPilot.Models.Enums;

/// <summary>
/// The closed priority set for an AI-recommended / created workflow task (spec §5.11/§5.12).
/// Persisted as the enum <b>name</b> string in <c>WorkflowTasks.Priority NVARCHAR(50)</c> via
/// <c>.HasConversion&lt;string&gt;()</c> (DBA DA-053 §P8.5.2). The full set is declared up front
/// (like <see cref="DocumentStatus"/>) so the enum does not churn. The recommendation/create
/// validators coerce any off-list value to <see cref="Normal"/> (ADR §5).
/// </summary>
public enum WorkflowPriority
{
    /// <summary>Low priority.</summary>
    Low,

    /// <summary>Normal priority — the default / coercion target for off-list values.</summary>
    Normal,

    /// <summary>High priority (e.g. a contract → LegalReview, §5.11).</summary>
    High,
}
