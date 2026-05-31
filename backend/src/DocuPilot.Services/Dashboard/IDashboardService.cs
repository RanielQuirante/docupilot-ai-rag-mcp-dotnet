using DocuPilot.Models.Contracts;

namespace DocuPilot.Services.Dashboard;

/// <summary>
/// Phase-9 dashboard read service (DA-058, spec §11.1). Composes three EFFICIENT aggregate repo
/// queries (a <c>GROUP BY Status</c> over Documents, an <c>Open</c> <c>COUNT</c> over WorkflowTasks,
/// and a <c>GROUP BY Classification</c> over DocumentClassifications) into the at-a-glance
/// <see cref="DashboardStats"/>. Pure read — no audit write, never 404s (an empty DB ⇒ all-zeros).
/// </summary>
public interface IDashboardService
{
    /// <summary>Builds the dashboard stats from the three aggregate queries (no row materialization, no N+1).</summary>
    Task<DashboardStats> GetStatsAsync(CancellationToken ct);
}
