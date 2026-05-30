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
/// Unit tests for <see cref="ClassificationService"/> — the Phase-4 classification + metadata
/// orchestrator. The <see cref="ILlmClient"/> is STUBBED (deterministic, no network) so the
/// parse/validate/coerce pipeline and the state machine are fully exercised offline. Covers:
/// happy classification + metadata, off-taxonomy → Unknown coercion, confidence clamp,
/// malformed-metadata-JSON → "{}", unparseable-classification → retry → Failed, and
/// LLM-unreachable → stays TextExtracted (transient). The UnitOfWork mock runs the staged action
/// (DA-024 pattern) so the transactional writes execute against the mocked repositories.
/// </summary>
public sealed class ClassificationServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly Mock<IDocumentRepository> _documents = new();
    private readonly Mock<IDocumentTextRepository> _texts = new();
    private readonly Mock<IDocumentClassificationRepository> _classifications = new();
    private readonly Mock<IExtractedMetadataRepository> _metadata = new();
    private readonly Mock<IAuditRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILlmClient> _llm = new();
    private readonly Mock<IPromptProvider> _prompts = new();
    private readonly FakeTimeProvider _timeProvider = new(FixedNow);

    public ClassificationServiceTests()
    {
        _unitOfWork
            .Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((action, ct) => action(ct));

        _prompts.Setup(p => p.BuildClassificationPrompt(It.IsAny<string>())).Returns("classify-prompt");
        _prompts.Setup(p => p.BuildMetadataPrompt(It.IsAny<string>(), It.IsAny<string>())).Returns("metadata-prompt");
    }

    private ClassificationService CreateSut(LlmOptions? options = null) => new(
        _documents.Object,
        _texts.Object,
        _classifications.Object,
        _metadata.Object,
        _audit.Object,
        _unitOfWork.Object,
        _llm.Object,
        _prompts.Object,
        _timeProvider,
        Options.Create(options ?? new LlmOptions { Model = "llama3.2:3b" }),
        NullLogger<ClassificationService>.Instance);

    private Document TextExtractedDoc(Guid id) => new()
    {
        Id = id,
        FileName = "doc.txt",
        ContentType = "text/plain",
        FilePath = "2026/05/30/key.txt",
        SizeBytes = 10,
        Status = DocumentStatus.TextExtracted,
        UploadedAt = FixedNow.UtcDateTime,
        ProcessedAt = FixedNow.UtcDateTime,
    };

    private void SetupClaimable(Document doc, bool claimWins = true, string content = "this is an invoice for $500")
    {
        _documents.Setup(r => r.GetByIdAsync(doc.Id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);
        _documents.Setup(r => r.TryClaimForClassificationAsync(doc.Id, It.IsAny<CancellationToken>())).ReturnsAsync(claimWins);
        _texts.Setup(t => t.GetByDocumentIdAsync(doc.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentText { Id = Guid.CreateVersion7(), DocumentId = doc.Id, Content = content, CharCount = content.Length, ExtractedAt = FixedNow.UtcDateTime });
    }

    /// <summary>Stubs the two sequential LLM calls: first = classification JSON, second = metadata JSON.</summary>
    private void SetupLlmSequence(string classificationJson, string metadataJson)
    {
        _llm.SetupSequence(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse(classificationJson, "llama3.2:3b", TimeSpan.Zero))
            .ReturnsAsync(new LlmResponse(metadataJson, "llama3.2:3b", TimeSpan.Zero));
    }

    [Fact]
    public async Task ClassifyAsync_HappyPath_PersistsClassificationMetadataAndClassified()
    {
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        SetupClaimable(doc);
        SetupLlmSequence(
            "{\"classification\":\"Invoice\",\"confidence\":0.93,\"reason\":\"Has an invoice number and amount.\"}",
            "{\"invoiceNumber\":\"INV-1\",\"amount\":500}");

        DocumentClassification? savedClass = null;
        ExtractedMetadata? savedMeta = null;
        _classifications.Setup(r => r.UpsertAsync(It.IsAny<DocumentClassification>(), It.IsAny<CancellationToken>()))
            .Callback<DocumentClassification, CancellationToken>((c, _) => savedClass = c).Returns(Task.CompletedTask);
        _metadata.Setup(r => r.UpsertAsync(It.IsAny<ExtractedMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<ExtractedMetadata, CancellationToken>((m, _) => savedMeta = m).Returns(Task.CompletedTask);

        var actions = new List<string>();
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => actions.Add(a.Action)).Returns(Task.CompletedTask);

        var outcome = await CreateSut().ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Succeeded);
        doc.Status.Should().Be(DocumentStatus.Classified);
        doc.ProcessedAt.Should().Be(FixedNow.UtcDateTime);
        doc.FailureReason.Should().BeNull();

        savedClass.Should().NotBeNull();
        savedClass!.Classification.Should().Be(DocumentCategory.Invoice);
        savedClass.Confidence.Should().Be(0.93m);
        savedClass.Reason.Should().Contain("invoice number");
        savedClass.Model.Should().Be("llama3.2:3b");

        savedMeta.Should().NotBeNull();
        savedMeta!.MetadataJson.Should().Contain("invoiceNumber");

        actions.Should().ContainInOrder("ClassificationStarted", "ClassificationSucceeded");
    }

    [Fact]
    public async Task ClassifyAsync_OffTaxonomyCategory_CoercesToUnknown()
    {
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        SetupClaimable(doc);
        SetupLlmSequence(
            "{\"classification\":\"Spaceship Manual\",\"confidence\":0.4,\"reason\":\"n/a\"}",
            "{}");

        DocumentClassification? savedClass = null;
        _classifications.Setup(r => r.UpsertAsync(It.IsAny<DocumentClassification>(), It.IsAny<CancellationToken>()))
            .Callback<DocumentClassification, CancellationToken>((c, _) => savedClass = c).Returns(Task.CompletedTask);

        var outcome = await CreateSut().ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Succeeded);
        savedClass!.Classification.Should().Be(DocumentCategory.Unknown);
        doc.Status.Should().Be(DocumentStatus.Classified); // off-taxonomy is coerced, NOT failed
    }

    [Theory]
    [InlineData("1.5", 1.0)]   // over 1 → clamped to 1
    [InlineData("-0.2", 0.0)]  // below 0 → clamped to 0
    public async Task ClassifyAsync_ConfidenceOutOfRange_ClampsToUnitInterval(string raw, double expected)
    {
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        SetupClaimable(doc);
        SetupLlmSequence(
            $"{{\"classification\":\"Contract\",\"confidence\":{raw},\"reason\":\"x\"}}",
            "{}");

        DocumentClassification? savedClass = null;
        _classifications.Setup(r => r.UpsertAsync(It.IsAny<DocumentClassification>(), It.IsAny<CancellationToken>()))
            .Callback<DocumentClassification, CancellationToken>((c, _) => savedClass = c).Returns(Task.CompletedTask);

        var outcome = await CreateSut().ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Succeeded);
        savedClass!.Confidence.Should().Be((decimal)expected);
    }

    [Fact]
    public async Task ClassifyAsync_NonObjectMetadata_StoresEmptyObjectButStillSucceeds()
    {
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        SetupClaimable(doc);
        SetupLlmSequence(
            "{\"classification\":\"Contract\",\"confidence\":0.8,\"reason\":\"x\"}",
            "[\"this\",\"is\",\"an\",\"array\"]"); // not an object → coerce to {}

        ExtractedMetadata? savedMeta = null;
        _metadata.Setup(r => r.UpsertAsync(It.IsAny<ExtractedMetadata>(), It.IsAny<CancellationToken>()))
            .Callback<ExtractedMetadata, CancellationToken>((m, _) => savedMeta = m).Returns(Task.CompletedTask);

        var outcome = await CreateSut().ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Succeeded);
        doc.Status.Should().Be(DocumentStatus.Classified);
        savedMeta!.MetadataJson.Should().Be("{}");
    }

    [Fact]
    public async Task ClassifyAsync_UnparseableClassification_RetriesThenFails()
    {
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        SetupClaimable(doc);
        // Every classification call returns non-JSON garbage → exhaust retries → Failed.
        _llm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("I think this might be a contract, not sure.", "llama3.2:3b", TimeSpan.Zero));

        var actions = new List<string>();
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => actions.Add(a.Action)).Returns(Task.CompletedTask);

        var outcome = await CreateSut(new LlmOptions { MaxAttempts = 2 }).ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Failed);
        doc.Status.Should().Be(DocumentStatus.Failed);
        doc.FailureReason.Should().Contain("unparseable");
        actions.Should().ContainInOrder("ClassificationStarted", "ClassificationFailed");
        // Retried exactly MaxAttempts times for the classification call.
        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _classifications.Verify(r => r.UpsertAsync(It.IsAny<DocumentClassification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClassifyAsync_LlmUnreachable_StaysTextExtractedAndReturnsTransient()
    {
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        SetupClaimable(doc);
        _llm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmUnavailableException("server unreachable"));

        var actions = new List<string>();
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => actions.Add(a.Action)).Returns(Task.CompletedTask);

        var outcome = await CreateSut().ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Transient);
        // Claim rolled back — doc left TextExtracted to retry, NOT Failed (no backlog poisoning).
        doc.Status.Should().Be(DocumentStatus.TextExtracted);
        doc.FailureReason.Should().BeNull();
        actions.Should().NotContain("ClassificationFailed");
        _classifications.Verify(r => r.UpsertAsync(It.IsAny<DocumentClassification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClassifyAsync_ClaimLost_ReturnsNotClaimedAndDoesNotCallLlm()
    {
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        SetupClaimable(doc, claimWins: false);

        var outcome = await CreateSut().ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.NotClaimed);
        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClassifyAsync_MissingDocument_ReturnsNotFound()
    {
        var id = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Document?)null);

        var outcome = await CreateSut().ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.NotFound);
    }

    [Fact]
    public async Task ClassifyAsync_NoExtractedText_FailsAsContentFault()
    {
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);
        _documents.Setup(r => r.TryClaimForClassificationAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _texts.Setup(t => t.GetByDocumentIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((DocumentText?)null);

        var outcome = await CreateSut().ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Failed);
        doc.Status.Should().Be(DocumentStatus.Failed);
        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClassifyAsync_PromptWrappedJson_StillParses()
    {
        // JSON-mode usually prevents prose wrappers, but be defensive: extract the object.
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        SetupClaimable(doc);
        SetupLlmSequence(
            "Here is the result:\n```json\n{\"classification\":\"Legal Document\",\"confidence\":0.7,\"reason\":\"r\"}\n```",
            "Sure: {\"party\":\"Acme\"}");

        DocumentClassification? savedClass = null;
        _classifications.Setup(r => r.UpsertAsync(It.IsAny<DocumentClassification>(), It.IsAny<CancellationToken>()))
            .Callback<DocumentClassification, CancellationToken>((c, _) => savedClass = c).Returns(Task.CompletedTask);

        var outcome = await CreateSut().ClassifyAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Succeeded);
        savedClass!.Classification.Should().Be(DocumentCategory.LegalDocument);
    }

    [Fact]
    public async Task RecoverStaleClassifying_StaleDoc_ResetsToTextExtracted()
    {
        var id = Guid.CreateVersion7();
        var doc = TextExtractedDoc(id);
        doc.Status = DocumentStatus.Classifying;

        _documents.Setup(r => r.GetStaleClassifyingIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id });
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);

        AuditLog? written = null;
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => written = a).Returns(Task.CompletedTask);

        var reset = await CreateSut().RecoverStaleClassifyingAsync(TimeSpan.FromMinutes(15), CancellationToken.None);

        reset.Should().Be(1);
        doc.Status.Should().Be(DocumentStatus.TextExtracted);
        written!.Action.Should().Be(nameof(AuditAction.ReprocessQueued));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
