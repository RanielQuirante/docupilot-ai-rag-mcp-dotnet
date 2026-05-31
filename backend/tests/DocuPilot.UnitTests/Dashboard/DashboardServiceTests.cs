using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Dashboard;
using FluentAssertions;
using Moq;

namespace DocuPilot.UnitTests.Dashboard;

/// <summary>
/// Unit tests for <see cref="DashboardService"/> — the Phase-9 stats composer (DA-058). All three
/// aggregate repos are mocked (no DB). Covers: the status-bucket roll-up (total / pending-processing
/// union / ready-for-search / failed); pending-workflow-task COUNT passthrough; the classification
/// breakdown grouping + display-string mapping + ordering; the all-empty (zero) case; and that
/// brand-new <c>Uploaded</c> docs are NOT counted as pending processing.
/// </summary>
public sealed class DashboardServiceTests
{
    private readonly Mock<IDocumentRepository> _documents = new();
    private readonly Mock<IWorkflowTaskRepository> _tasks = new();
    private readonly Mock<IDocumentClassificationRepository> _classifications = new();

    private DashboardService CreateSut() => new(_documents.Object, _tasks.Object, _classifications.Object);

    private void SetupStatuses(IReadOnlyDictionary<DocumentStatus, int> counts) =>
        _documents.Setup(r => r.CountByStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(counts);

    private void SetupOpenTasks(int count) =>
        _tasks.Setup(r => r.CountByStatusAsync(WorkflowTaskStatus.Open, It.IsAny<CancellationToken>())).ReturnsAsync(count);

    private void SetupCategories(IReadOnlyDictionary<DocumentCategory, int> counts) =>
        _classifications.Setup(r => r.CountByCategoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(counts);

    [Fact]
    public async Task GetStatsAsync_RollsUpEachStatusBucketAndTasks()
    {
        SetupStatuses(new Dictionary<DocumentStatus, int>
        {
            [DocumentStatus.Uploaded] = 1,
            [DocumentStatus.Queued] = 2,
            [DocumentStatus.ExtractingText] = 1,
            [DocumentStatus.Classified] = 1,
            [DocumentStatus.GeneratingEmbeddings] = 1,
            [DocumentStatus.ReadyForSearch] = 8,
            [DocumentStatus.Failed] = 1,
        });
        SetupOpenTasks(3);
        SetupCategories(new Dictionary<DocumentCategory, int>());

        var stats = await CreateSut().GetStatsAsync(default);

        stats.TotalDocuments.Should().Be(15); // sum of all status buckets
        // pending-processing = Queued(2)+ExtractingText(1)+Classified(1)+GeneratingEmbeddings(1) = 5.
        // Uploaded(1) is NOT pending; ReadyForSearch + Failed are their own buckets.
        stats.PendingProcessing.Should().Be(5);
        stats.ReadyForSearch.Should().Be(8);
        stats.Failed.Should().Be(1);
        stats.PendingWorkflowTasks.Should().Be(3);
    }

    [Fact]
    public async Task GetStatsAsync_UploadedDocsAreNotPendingProcessing()
    {
        SetupStatuses(new Dictionary<DocumentStatus, int> { [DocumentStatus.Uploaded] = 4 });
        SetupOpenTasks(0);
        SetupCategories(new Dictionary<DocumentCategory, int>());

        var stats = await CreateSut().GetStatsAsync(default);

        stats.TotalDocuments.Should().Be(4);
        stats.PendingProcessing.Should().Be(0);
        stats.ReadyForSearch.Should().Be(0);
        stats.Failed.Should().Be(0);
    }

    [Fact]
    public async Task GetStatsAsync_IncludesAllInFlightStatusesInPendingProcessing()
    {
        SetupStatuses(new Dictionary<DocumentStatus, int>
        {
            [DocumentStatus.Queued] = 1,
            [DocumentStatus.ExtractingText] = 1,
            [DocumentStatus.TextExtracted] = 1,
            [DocumentStatus.Classifying] = 1,
            [DocumentStatus.Classified] = 1,
            [DocumentStatus.GeneratingEmbeddings] = 1,
        });
        SetupOpenTasks(0);
        SetupCategories(new Dictionary<DocumentCategory, int>());

        var stats = await CreateSut().GetStatsAsync(default);

        stats.PendingProcessing.Should().Be(6);
        stats.TotalDocuments.Should().Be(6);
    }

    [Fact]
    public async Task GetStatsAsync_GroupsClassificationBreakdownWithDisplayNamesOrderedByCountDesc()
    {
        SetupStatuses(new Dictionary<DocumentStatus, int> { [DocumentStatus.ReadyForSearch] = 8 });
        SetupOpenTasks(0);
        SetupCategories(new Dictionary<DocumentCategory, int>
        {
            [DocumentCategory.Contract] = 4,
            [DocumentCategory.Invoice] = 3,
            [DocumentCategory.EmployeeRecord] = 1,
        });

        var stats = await CreateSut().GetStatsAsync(default);

        stats.ClassificationBreakdown.Should().HaveCount(3);
        stats.ClassificationBreakdown[0].Category.Should().Be("Contract");
        stats.ClassificationBreakdown[0].Count.Should().Be(4);
        stats.ClassificationBreakdown[1].Category.Should().Be("Invoice");
        // Enum-to-display-string mapping (EmployeeRecord → "Employee Record").
        stats.ClassificationBreakdown[2].Category.Should().Be("Employee Record");
        stats.ClassificationBreakdown[2].Count.Should().Be(1);
    }

    [Fact]
    public async Task GetStatsAsync_EmptyDatabase_ReturnsAllZerosAndEmptyBreakdown()
    {
        SetupStatuses(new Dictionary<DocumentStatus, int>());
        SetupOpenTasks(0);
        SetupCategories(new Dictionary<DocumentCategory, int>());

        var stats = await CreateSut().GetStatsAsync(default);

        stats.TotalDocuments.Should().Be(0);
        stats.PendingProcessing.Should().Be(0);
        stats.ReadyForSearch.Should().Be(0);
        stats.Failed.Should().Be(0);
        stats.PendingWorkflowTasks.Should().Be(0);
        stats.ClassificationBreakdown.Should().BeEmpty();
    }
}
