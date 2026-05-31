using DocuPilot.Models.Contracts;
using DocuPilot.Services.Common;

namespace DocuPilot.Services.Audit;

/// <summary>
/// Phase-9 global audit-log read service (DA-058, spec §11.8). Returns a newest-first, paged,
/// optionally-filtered view of the append-only <c>AuditLogs</c> table for the audit-logs page —
/// the visible proof of the §5.12 "every AI action is audited" story. Pure read — no audit write.
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// Lists audit rows newest-first as a <see cref="PagedResult{T}"/> of <see cref="AuditLogListItem"/>.
    /// <paramref name="page"/>/<paramref name="pageSize"/> are normalized internally (page floor 1,
    /// pageSize default 50 capped at 100 — mirrors the document list). Optional filters:
    /// <paramref name="entityId"/> (a non-empty GUID) and <paramref name="action"/> (validated against
    /// the <c>AuditAction</c> enum names — an unrecognized value ⇒ <see cref="AuditLogListOutcomeKind.InvalidAction"/>).
    /// </summary>
    Task<AuditLogListOutcome> ListAsync(int page, int pageSize, Guid? entityId, string? action, CancellationToken ct);
}

/// <summary>Outcome kind for <see cref="IAuditLogService.ListAsync"/> (drives the controller's status code).</summary>
public enum AuditLogListOutcomeKind
{
    /// <summary>A page was produced (→ 200).</summary>
    Success,

    /// <summary>The <c>action</c> filter was not a recognized <c>AuditAction</c> name (→ 400).</summary>
    InvalidAction,
}

/// <summary>The result of <see cref="IAuditLogService.ListAsync"/>.</summary>
public sealed class AuditLogListOutcome
{
    private AuditLogListOutcome(AuditLogListOutcomeKind kind, PagedResult<AuditLogListItem>? page, string? error)
    {
        Kind = kind;
        Page = page;
        Error = error;
    }

    public AuditLogListOutcomeKind Kind { get; }

    public PagedResult<AuditLogListItem>? Page { get; }

    /// <summary>A human-readable message for the <see cref="AuditLogListOutcomeKind.InvalidAction"/> path.</summary>
    public string? Error { get; }

    public static AuditLogListOutcome Success(PagedResult<AuditLogListItem> page) =>
        new(AuditLogListOutcomeKind.Success, page, null);

    public static AuditLogListOutcome InvalidAction(string error) =>
        new(AuditLogListOutcomeKind.InvalidAction, null, error);
}
