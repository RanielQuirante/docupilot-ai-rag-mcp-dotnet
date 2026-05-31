namespace DocuPilot.Models.Contracts;

/// <summary>
/// The dashboard "at-a-glance" stats response (<c>GET /api/dashboard/stats</c>, spec §11.1).
/// All counts are computed from EFFICIENT aggregate queries (<c>COUNT</c> + one <c>GROUP BY</c>) —
/// no rows are materialized. An empty database yields all-zero counts and an empty breakdown
/// (never a 404). Read-only and additive (Phase 9, DA-058).
/// </summary>
/// <param name="TotalDocuments">Total number of documents across all statuses.</param>
/// <param name="PendingProcessing">Documents in a non-terminal, in-flight processing status (see DA-058 notes): Queued, ExtractingText, TextExtracted, Classifying, Classified, GeneratingEmbeddings — i.e. NOT ReadyForSearch and NOT Failed. Newly Uploaded docs that have not yet been queued are NOT counted as pending processing.</param>
/// <param name="ReadyForSearch">Documents with <c>Status == ReadyForSearch</c>.</param>
/// <param name="Failed">Documents with <c>Status == Failed</c>.</param>
/// <param name="PendingWorkflowTasks">Workflow tasks with <c>Status == Open</c>.</param>
/// <param name="ClassificationBreakdown">Per-category document counts, grouped over <c>DocumentClassifications</c> (the newest taxonomy). Categories with no classified documents are omitted.</param>
public sealed record DashboardStats(
    int TotalDocuments,
    int PendingProcessing,
    int ReadyForSearch,
    int Failed,
    int PendingWorkflowTasks,
    IReadOnlyList<ClassificationBreakdownItem> ClassificationBreakdown);

/// <summary>
/// A single row of the dashboard classification breakdown — a document category (the spec
/// display string, e.g. <c>"Contract"</c>) and how many classified documents fall in it.
/// </summary>
/// <param name="Category">The category display string (e.g. "Contract", "Invoice", "Unknown").</param>
/// <param name="Count">The number of classified documents in this category.</param>
public sealed record ClassificationBreakdownItem(string Category, int Count);
