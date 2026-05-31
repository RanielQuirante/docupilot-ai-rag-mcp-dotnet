using DocuPilot.Models.Contracts;
using DocuPilot.Services.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Phase-9 dashboard endpoint (DA-058, spec §11.1). Thin controller — delegates to
/// <see cref="IDashboardService"/> and returns the Contract. No business logic. Read-only and
/// additive; always <c>200</c> (an empty database yields all-zero counts + an empty breakdown).
/// </summary>
[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboard;

    public DashboardController(IDashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    /// <summary>
    /// Returns the at-a-glance dashboard stats: total / pending-processing / ready-for-search /
    /// failed document counts, the count of open workflow tasks, and the per-category classification
    /// breakdown. Always <c>200</c>.
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(DashboardStats), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardStats>> GetStats(CancellationToken ct)
    {
        var stats = await _dashboard.GetStatsAsync(ct);
        return Ok(stats);
    }
}
