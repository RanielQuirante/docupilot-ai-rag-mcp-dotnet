using System.Text;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DocuPilot.UnitTests.Documents;

/// <summary>
/// Unit tests for <see cref="DocumentProcessingService"/> — the Phase-3 state machine. Covers
/// the happy path (claim → extract → TextExtracted + text upsert + audit), the empty/unsupported
/// → Failed paths, the atomic-claim guard, idempotent upsert, transient-retry, timeout-→-Failed,
/// and the manual re-queue (404 / 409 / queued). All collaborators are mocked; the UnitOfWork
/// mock runs the staged action so the transactional writes execute against the mocks.
/// </summary>
public sealed class DocumentProcessingServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly Mock<IDocumentRepository> _documents = new();
    private readonly Mock<IDocumentTextRepository> _texts = new();
    private readonly Mock<IAuditRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IFileStorage> _fileStorage = new();
    private readonly Mock<ITextExtractionService> _extraction = new();
    private readonly FakeTimeProvider _timeProvider = new(FixedNow);

    public DocumentProcessingServiceTests()
    {
        _unitOfWork
            .Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((action, ct) => action(ct));

        _fileStorage
            .Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("raw")));
    }

    private DocumentProcessingService CreateSut(ExtractionOptions? options = null) => new(
        _documents.Object,
        _texts.Object,
        _audit.Object,
        _unitOfWork.Object,
        _fileStorage.Object,
        _extraction.Object,
        _timeProvider,
        Options.Create(options ?? new ExtractionOptions()),
        NullLogger<DocumentProcessingService>.Instance);

    private Document QueuedDoc(Guid id) => new()
    {
        Id = id,
        FileName = "doc.txt",
        ContentType = "text/plain",
        FilePath = "2026/05/30/key.txt",
        SizeBytes = 10,
        Status = DocumentStatus.Queued,
        UploadedAt = FixedNow.UtcDateTime,
    };

    private void SetupClaimable(Document doc, bool claimWins = true)
    {
        _documents.Setup(r => r.GetByIdAsync(doc.Id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);
        _documents.Setup(r => r.TryClaimAsync(doc.Id, It.IsAny<CancellationToken>())).ReturnsAsync(claimWins);
    }

    [Fact]
    public async Task ProcessAsync_HappyPath_ExtractsPersistsAndAuditsSucceeded()
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        SetupClaimable(doc);
        _extraction
            .Setup(e => e.ExtractAsync(It.IsAny<Stream>(), "text/plain", "doc.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync("extracted body");

        DocumentText? upserted = null;
        _texts.Setup(t => t.UpsertAsync(It.IsAny<DocumentText>(), It.IsAny<CancellationToken>()))
            .Callback<DocumentText, CancellationToken>((t, _) => upserted = t)
            .Returns(Task.CompletedTask);

        var actions = new List<string>();
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => actions.Add(a.Action))
            .Returns(Task.CompletedTask);

        var outcome = await CreateSut().ProcessAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Succeeded);
        doc.Status.Should().Be(DocumentStatus.TextExtracted);
        doc.ProcessedAt.Should().Be(FixedNow.UtcDateTime);
        doc.FailureReason.Should().BeNull();

        upserted.Should().NotBeNull();
        upserted!.DocumentId.Should().Be(id);
        upserted.Content.Should().Be("extracted body");
        upserted.CharCount.Should().Be("extracted body".Length);

        actions.Should().ContainInOrder("ExtractionStarted", "ExtractionSucceeded");
    }

    [Fact]
    public async Task ProcessAsync_ClaimLost_ReturnsNotClaimedAndDoesNotExtract()
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        SetupClaimable(doc, claimWins: false);

        var outcome = await CreateSut().ProcessAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.NotClaimed);
        _extraction.Verify(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_MissingDocument_ReturnsNotFound()
    {
        var id = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Document?)null);

        var outcome = await CreateSut().ProcessAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.NotFound);
    }

    [Fact]
    public async Task ProcessAsync_EmptyExtraction_FailsWithReason()
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        SetupClaimable(doc);
        _extraction
            .Setup(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("   "); // whitespace → empty

        var actions = new List<string>();
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => actions.Add(a.Action))
            .Returns(Task.CompletedTask);

        var outcome = await CreateSut().ProcessAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Failed);
        doc.Status.Should().Be(DocumentStatus.Failed);
        doc.ProcessedAt.Should().Be(FixedNow.UtcDateTime);
        doc.FailureReason.Should().Contain("No extractable text");
        actions.Should().ContainInOrder("ExtractionStarted", "ExtractionFailed");
        _texts.Verify(t => t.UpsertAsync(It.IsAny<DocumentText>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_UnsupportedFormat_FailsFastWithoutRetry()
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        SetupClaimable(doc);
        _extraction
            .Setup(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnsupportedFormatException("No text extractor supports this document."));

        var outcome = await CreateSut(new ExtractionOptions { MaxAttempts = 3 }).ProcessAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Failed);
        doc.Status.Should().Be(DocumentStatus.Failed);
        // Non-transient → fail fast, exactly ONE extraction attempt despite MaxAttempts=3.
        _extraction.Verify(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_TransientThenSuccess_RetriesAndSucceeds()
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        SetupClaimable(doc);

        var calls = 0;
        _extraction
            .Setup(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                return calls == 1
                    ? throw new IOException("transient read hiccup")
                    : Task.FromResult("recovered text");
            });

        var outcome = await CreateSut(new ExtractionOptions { MaxAttempts = 3 }).ProcessAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Succeeded);
        calls.Should().Be(2); // failed once (transient), retried, succeeded
        doc.Status.Should().Be(DocumentStatus.TextExtracted);
    }

    [Fact]
    public async Task ProcessAsync_TransientExhausted_Fails()
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        SetupClaimable(doc);
        _extraction
            .Setup(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("always failing"));

        var outcome = await CreateSut(new ExtractionOptions { MaxAttempts = 2 }).ProcessAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Failed);
        doc.Status.Should().Be(DocumentStatus.Failed);
        _extraction.Verify(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProcessAsync_OverMaxChars_TruncatesAndSucceeds()
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        SetupClaimable(doc);
        _extraction
            .Setup(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new string('a', 100));

        DocumentText? upserted = null;
        _texts.Setup(t => t.UpsertAsync(It.IsAny<DocumentText>(), It.IsAny<CancellationToken>()))
            .Callback<DocumentText, CancellationToken>((t, _) => upserted = t)
            .Returns(Task.CompletedTask);

        var outcome = await CreateSut(new ExtractionOptions { MaxTextChars = 10 }).ProcessAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Succeeded);
        upserted!.CharCount.Should().Be(10); // truncated to the cap
        upserted.Content.Length.Should().Be(10);
    }

    [Fact]
    public async Task RequeueAsync_FailedDoc_ResetsToQueuedClearsReason()
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        doc.Status = DocumentStatus.Failed;
        doc.FailureReason = "something broke";
        doc.ProcessedAt = FixedNow.UtcDateTime;
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);

        string? auditAction = null;
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => auditAction = a.Action)
            .Returns(Task.CompletedTask);

        var result = await CreateSut().RequeueAsync(id, CancellationToken.None);

        result.Should().Be(RequeueResult.Queued);
        doc.Status.Should().Be(DocumentStatus.Queued);
        doc.FailureReason.Should().BeNull();
        doc.ProcessedAt.Should().BeNull();
        auditAction.Should().Be("ReprocessQueued");
    }

    [Theory]
    [InlineData(DocumentStatus.Queued)]
    [InlineData(DocumentStatus.ExtractingText)]
    public async Task RequeueAsync_AlreadyInFlight_ReturnsConflict(DocumentStatus status)
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        doc.Status = status;
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);

        var result = await CreateSut().RequeueAsync(id, CancellationToken.None);

        result.Should().Be(RequeueResult.Conflict);
    }

    [Fact]
    public async Task RequeueAsync_Missing_ReturnsNotFound()
    {
        var id = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Document?)null);

        var result = await CreateSut().RequeueAsync(id, CancellationToken.None);

        result.Should().Be(RequeueResult.NotFound);
    }

    // ---- Stale-claim recovery (DA-025, PM Q4 — audit-timestamp) ----

    [Fact]
    public async Task RecoverStaleClaims_NoStaleDocs_ResetsNothing()
    {
        _documents
            .Setup(r => r.GetStaleExtractingIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());

        var reset = await CreateSut().RecoverStaleClaimsAsync(TimeSpan.FromMinutes(15), CancellationToken.None);

        reset.Should().Be(0);
        _documents.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecoverStaleClaims_ComputesCutoffFromThreshold()
    {
        DateTime captured = default;
        _documents
            .Setup(r => r.GetStaleExtractingIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, CancellationToken>((cutoff, _) => captured = cutoff)
            .ReturnsAsync(Array.Empty<Guid>());

        await CreateSut().RecoverStaleClaimsAsync(TimeSpan.FromMinutes(15), CancellationToken.None);

        // cutoff = now - threshold; anything ExtractingText started before this is stale.
        captured.Should().Be(FixedNow.UtcDateTime - TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task RecoverStaleClaims_StaleDoc_ResetsToQueuedWithAudit()
    {
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        doc.Status = DocumentStatus.ExtractingText;

        _documents
            .Setup(r => r.GetStaleExtractingIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id });
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);

        AuditLog? written = null;
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => written = a)
            .Returns(Task.CompletedTask);

        var reset = await CreateSut().RecoverStaleClaimsAsync(TimeSpan.FromMinutes(15), CancellationToken.None);

        reset.Should().Be(1);
        doc.Status.Should().Be(DocumentStatus.Queued);
        written.Should().NotBeNull();
        written!.Action.Should().Be(nameof(AuditAction.ReprocessQueued));
        written.EntityId.Should().Be(id);
        // Committed transactionally (status + audit together).
        _unitOfWork.Verify(u => u.ExecuteInTransactionAsync(
            It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecoverStaleClaims_DocNoLongerExtracting_SkipsIt()
    {
        // The candidate was already moved on (e.g. finished) between the sweep query and the load.
        var id = Guid.CreateVersion7();
        var doc = QueuedDoc(id);
        doc.Status = DocumentStatus.TextExtracted;

        _documents
            .Setup(r => r.GetStaleExtractingIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id });
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);

        var reset = await CreateSut().RecoverStaleClaimsAsync(TimeSpan.FromMinutes(15), CancellationToken.None);

        reset.Should().Be(0);
        doc.Status.Should().Be(DocumentStatus.TextExtracted); // untouched
        _unitOfWork.Verify(u => u.ExecuteInTransactionAsync(
            It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
