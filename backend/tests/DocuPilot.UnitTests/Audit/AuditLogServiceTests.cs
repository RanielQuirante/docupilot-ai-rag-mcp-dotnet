using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Audit;
using FluentAssertions;
using Moq;

namespace DocuPilot.UnitTests.Audit;

/// <summary>
/// Unit tests for <see cref="AuditLogService"/> — the Phase-9 global audit-log read service (DA-058).
/// The <see cref="IAuditRepository"/> is mocked (no DB). Covers: success mapping into a
/// <c>PagedResult&lt;AuditLogListItem&gt;</c> (newest-first ordering preserved from the repo);
/// entityId + action filter passthrough; the empty-GUID-means-no-filter rule; invalid action ⇒
/// InvalidAction (→ 400) with NO repo call; pageSize cap at 100; default pageSize 50; page floor 1.
/// </summary>
public sealed class AuditLogServiceTests
{
    private readonly Mock<IAuditRepository> _audit = new();

    private AuditLogService CreateSut() => new(_audit.Object);

    private static AuditLog Row(string entityName, Guid entityId, AuditAction action, DateTime createdAt) => new()
    {
        Id = Guid.CreateVersion7(),
        EntityName = entityName,
        EntityId = entityId,
        Action = action.ToString(),
        DetailsJson = "{}",
        CreatedAt = createdAt,
    };

    private void SetupRepo(IReadOnlyList<AuditLog> items, long total) =>
        _audit.Setup(r => r.ListAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((items, total));

    [Fact]
    public async Task ListAsync_MapsRowsToPagedResult_PreservingNewestFirstOrder()
    {
        var newer = Row("Document", Guid.CreateVersion7(), AuditAction.ClassificationSucceeded, new DateTime(2026, 5, 31, 10, 0, 0, DateTimeKind.Utc));
        var older = Row("WorkflowTool", Guid.CreateVersion7(), AuditAction.ToolSucceeded, new DateTime(2026, 5, 31, 9, 0, 0, DateTimeKind.Utc));
        SetupRepo([newer, older], 2);

        var outcome = await CreateSut().ListAsync(1, 50, null, null, default);

        outcome.Kind.Should().Be(AuditLogListOutcomeKind.Success);
        outcome.Page!.TotalCount.Should().Be(2);
        outcome.Page.Items.Should().HaveCount(2);
        outcome.Page.Items[0].CreatedAt.Should().BeAfter(outcome.Page.Items[1].CreatedAt);
        outcome.Page.Items[0].EntityName.Should().Be("Document");
        outcome.Page.Items[0].Action.Should().Be("ClassificationSucceeded");
    }

    [Fact]
    public async Task ListAsync_PassesEntityIdFilterThrough()
    {
        var entityId = Guid.CreateVersion7();
        SetupRepo([], 0);

        await CreateSut().ListAsync(1, 50, entityId, null, default);

        _audit.Verify(r => r.ListAsync(1, 50, entityId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_EmptyEntityId_TreatedAsNoFilter()
    {
        SetupRepo([], 0);

        await CreateSut().ListAsync(1, 50, Guid.Empty, null, default);

        _audit.Verify(r => r.ListAsync(1, 50, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_ValidActionFilter_PassesCanonicalEnumName()
    {
        SetupRepo([], 0);

        // Case-insensitive parse; the canonical enum-name string is what the column stores.
        await CreateSut().ListAsync(1, 50, null, "toolsucceeded", default);

        _audit.Verify(r => r.ListAsync(1, 50, null, "ToolSucceeded", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_InvalidAction_ReturnsInvalidAction_NoRepoCall()
    {
        var outcome = await CreateSut().ListAsync(1, 50, null, "NotARealAction", default);

        outcome.Kind.Should().Be(AuditLogListOutcomeKind.InvalidAction);
        outcome.Error.Should().NotBeNullOrWhiteSpace();
        _audit.Verify(r => r.ListAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListAsync_CapsPageSizeAt100()
    {
        SetupRepo([], 0);

        var outcome = await CreateSut().ListAsync(1, 5000, null, null, default);

        outcome.Page!.PageSize.Should().Be(100);
        _audit.Verify(r => r.ListAsync(1, 100, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_NonPositivePageSize_DefaultsTo50()
    {
        SetupRepo([], 0);

        var outcome = await CreateSut().ListAsync(1, 0, null, null, default);

        outcome.Page!.PageSize.Should().Be(50);
        _audit.Verify(r => r.ListAsync(1, 50, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_PageBelowOne_FloorsToOne()
    {
        SetupRepo([], 0);

        var outcome = await CreateSut().ListAsync(0, 50, null, null, default);

        outcome.Page!.Page.Should().Be(1);
        _audit.Verify(r => r.ListAsync(1, 50, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
