using DocuPilot.Models.Contracts;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;

namespace DocuPilot.Services.Dashboard;

/// <summary>
/// Phase-9 dashboard read service (DA-058). See <see cref="IDashboardService"/>. Composes three
/// EFFICIENT aggregate repo queries into the <see cref="DashboardStats"/> DTO — no entity rows are
/// loaded, no N+1. Thin business layer (the controller stays a pass-through); fully unit-testable
/// with mocked repos.
/// </summary>
public sealed class DashboardService : IDashboardService
{
    /// <summary>
    /// The in-flight, non-terminal processing statuses that roll up into "pending processing"
    /// (DA-058): a document that has entered the pipeline (Queued onward) but has not reached the
    /// ReadyForSearch terminal or Failed. A brand-new <c>Uploaded</c> doc is NOT yet pending
    /// processing, and ReadyForSearch / Failed are reported in their own buckets.
    /// </summary>
    private static readonly DocumentStatus[] PendingProcessingStatuses =
    [
        DocumentStatus.Queued,
        DocumentStatus.ExtractingText,
        DocumentStatus.TextExtracted,
        DocumentStatus.Classifying,
        DocumentStatus.Classified,
        DocumentStatus.GeneratingEmbeddings,
    ];

    private readonly IDocumentRepository _documents;
    private readonly IWorkflowTaskRepository _tasks;
    private readonly IDocumentClassificationRepository _classifications;

    public DashboardService(
        IDocumentRepository documents,
        IWorkflowTaskRepository tasks,
        IDocumentClassificationRepository classifications)
    {
        _documents = documents;
        _tasks = tasks;
        _classifications = classifications;
    }

    public async Task<DashboardStats> GetStatsAsync(CancellationToken ct)
    {
        // Three aggregate queries (GROUP BY Status, COUNT Open, GROUP BY Category) — no row loads.
        var statusCounts = await _documents.CountByStatusAsync(ct);
        var pendingTasks = await _tasks.CountByStatusAsync(WorkflowTaskStatus.Open, ct);
        var categoryCounts = await _classifications.CountByCategoryAsync(ct);

        var total = statusCounts.Values.Sum();
        var pendingProcessing = PendingProcessingStatuses.Sum(s => statusCounts.GetValueOrDefault(s));
        var readyForSearch = statusCounts.GetValueOrDefault(DocumentStatus.ReadyForSearch);
        var failed = statusCounts.GetValueOrDefault(DocumentStatus.Failed);

        // Stable, presentable ordering: highest count first, then category name. Map the enum to the
        // spec display string so the wire shape matches the rest of the API (DocumentCategoryNames).
        var breakdown = categoryCounts
            .Select(kvp => new ClassificationBreakdownItem(DocumentCategoryNames.ToDisplay(kvp.Key), kvp.Value))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .ToList();

        return new DashboardStats(
            total,
            pendingProcessing,
            readyForSearch,
            failed,
            pendingTasks,
            breakdown);
    }
}
