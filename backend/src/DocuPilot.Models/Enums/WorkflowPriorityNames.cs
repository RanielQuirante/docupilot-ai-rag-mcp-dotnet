namespace DocuPilot.Models.Enums;

/// <summary>
/// Coercion helper for <see cref="WorkflowPriority"/> — the single source of truth for parsing a
/// model-supplied / user-supplied priority string against the closed set, coercing anything off-list
/// (or null/whitespace) to <see cref="WorkflowPriority.Normal"/> (ADR §5 — "off-value → Normal").
/// Mirrors <see cref="DocumentCategoryNames.Coerce"/> so the recommendation validator and the
/// create-task validator never drift. The enum member names ARE the persisted strings.
/// </summary>
public static class WorkflowPriorityNames
{
    /// <summary>The allowed priority names, for prompt templating / tool input-schema enums.</summary>
    public static IReadOnlyList<string> Names { get; } = Enum.GetNames<WorkflowPriority>();

    /// <summary>
    /// Parses a priority string against the closed set (case/whitespace-tolerant), coercing anything
    /// off-list / null / whitespace to <see cref="WorkflowPriority.Normal"/>.
    /// </summary>
    public static WorkflowPriority Coerce(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return WorkflowPriority.Normal;
        }

        return Enum.TryParse<WorkflowPriority>(value.Trim(), ignoreCase: true, out var parsed)
               && Enum.IsDefined(parsed)
            ? parsed
            : WorkflowPriority.Normal;
    }

    /// <summary>
    /// Strict membership check (case/whitespace-tolerant) — used by the create-task validator to
    /// distinguish "off-enum" (400) from a real value. Returns <c>false</c> for null/blank/off-list.
    /// </summary>
    public static bool IsKnown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Enum.TryParse<WorkflowPriority>(value.Trim(), ignoreCase: true, out var parsed)
               && Enum.IsDefined(parsed);
    }
}
