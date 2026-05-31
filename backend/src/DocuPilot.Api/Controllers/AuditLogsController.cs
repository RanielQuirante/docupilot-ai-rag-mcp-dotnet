using DocuPilot.Models.Contracts;
using DocuPilot.Services.Audit;
using DocuPilot.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Phase-9 global audit-log endpoint (DA-058, spec §11.8). Thin controller — binds the query,
/// delegates to <see cref="IAuditLogService"/>, and maps the discriminated outcome to a status code.
/// Read-only and additive; reuses the Phase-2 <see cref="PagedResult{T}"/> envelope. No business logic.
/// </summary>
[ApiController]
[Route("api/audit-logs")]
public sealed class AuditLogsController : ControllerBase
{
    private readonly IAuditLogService _auditLogs;

    public AuditLogsController(IAuditLogService auditLogs)
    {
        _auditLogs = auditLogs;
    }

    /// <summary>
    /// Returns a page of audit-log entries newest-first (<c>CreatedAt DESC</c>). <c>page</c> defaults
    /// to 1, <c>pageSize</c> defaults to 50 and is capped at 100 (normalized in the service). Optional
    /// filters: <c>entityId</c> (GUID) and <c>action</c> (an <c>AuditAction</c> name). An unrecognized
    /// <c>action</c> ⇒ <c>400</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<AuditLogListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<AuditLogListItem>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? entityId = null,
        [FromQuery] string? action = null,
        CancellationToken ct = default)
    {
        var outcome = await _auditLogs.ListAsync(page, pageSize, entityId, action, ct);
        return outcome.Kind switch
        {
            AuditLogListOutcomeKind.Success => Ok(outcome.Page),
            _ => BadRequest(new { error = outcome.Error }),
        };
    }
}
