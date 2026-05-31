using DocuPilot.Models.Contracts;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Common;

namespace DocuPilot.Services.Audit;

/// <summary>
/// Phase-9 global audit-log read service (DA-058). See <see cref="IAuditLogService"/>. Normalizes
/// paging (mirrors the Phase-2 document-list cap), validates the optional <c>action</c> filter
/// against the <see cref="AuditAction"/> enum, delegates a single paged query to
/// <see cref="IAuditRepository.ListAsync"/>, and maps the rows to <see cref="AuditLogListItem"/>.
/// Pure read — no audit write.
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    private readonly IAuditRepository _audit;

    public AuditLogService(IAuditRepository audit)
    {
        _audit = audit;
    }

    public async Task<AuditLogListOutcome> ListAsync(int page, int pageSize, Guid? entityId, string? action, CancellationToken ct)
    {
        // Normalize paging (defense in depth — the same cap as GET /api/documents).
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        // An empty GUID is treated as "no filter" (consistent with the workflow-tasks list).
        var entityFilter = entityId is { } id && id != Guid.Empty ? entityId : null;

        // Validate the optional action filter against the AuditAction enum names — unknown ⇒ 400.
        string? actionFilter = null;
        if (!string.IsNullOrWhiteSpace(action))
        {
            if (!Enum.TryParse<AuditAction>(action.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return AuditLogListOutcome.InvalidAction(
                    $"action must be one of: {string.Join(", ", Enum.GetNames<AuditAction>())}.");
            }

            // Persist the canonical enum-name string so the query matches the stored Action column.
            actionFilter = parsed.ToString();
        }

        var (rows, totalCount) = await _audit.ListAsync(normalizedPage, normalizedPageSize, entityFilter, actionFilter, ct);

        var items = rows
            .Select(a => new AuditLogListItem(a.Id, a.EntityName, a.EntityId, a.Action, a.DetailsJson, a.CreatedAt))
            .ToList();

        return AuditLogListOutcome.Success(
            new PagedResult<AuditLogListItem>(items, normalizedPage, normalizedPageSize, totalCount));
    }
}
