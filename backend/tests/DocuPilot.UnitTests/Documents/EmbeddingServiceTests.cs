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
/// Unit tests for <see cref="EmbeddingService"/> — the Phase-5 embedding orchestrator. The
/// <see cref="IEmbeddingClient"/> and <see cref="IVectorStore"/> are STUBBED (no network); the
/// <see cref="IChunkingService"/> uses the REAL deterministic chunker. Covers: happy path
/// (chunk → embed → Qdrant upsert → SQL persist → ReadyForSearch); embedder-down → Transient (stays
/// Classified, nothing written); Qdrant-down → Transient; partial-embed-then-fault → nothing written;
/// re-embed idempotency (delete-first + deterministic point ids); claim race → NotClaimed; missing
/// doc → NotFound; no-text → Failed; stale-sweep reset. The UnitOfWork mock runs the staged action
/// (DA-032 pattern) so transactional writes execute against the mocked repositories.
/// </summary>
public sealed class EmbeddingServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IDocumentRepository> _documents = new();
    private readonly Mock<IDocumentTextRepository> _texts = new();
    private readonly Mock<IDocumentChunkRepository> _chunks = new();
    private readonly Mock<IAuditRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IEmbeddingClient> _embedder = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly RecursiveCharacterChunker _chunker =
        new(Options.Create(new ChunkingConfig { MaxChars = 40, OverlapChars = 0, MaxChunksPerDocument = 1000 }));
    private readonly FakeTimeProvider _timeProvider = new(FixedNow);

    public EmbeddingServiceTests()
    {
        _unitOfWork
            .Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((action, ct) => action(ct));

        _embedder.SetupGet(e => e.Dimensions).Returns(768);
        _embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingResult(new float[768], "nomic-embed-text", TimeSpan.Zero));
        _vectorStore.Setup(v => v.DeleteByDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _vectorStore.Setup(v => v.UpsertChunksAsync(It.IsAny<IReadOnlyList<ChunkVector>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private EmbeddingService CreateSut(EmbeddingOptions? options = null) => new(
        _documents.Object,
        _texts.Object,
        _chunks.Object,
        _audit.Object,
        _unitOfWork.Object,
        _chunker,
        _embedder.Object,
        _vectorStore.Object,
        _timeProvider,
        Options.Create(options ?? new EmbeddingOptions { Model = "nomic-embed-text", Dimensions = 768, MaxAttempts = 2 }),
        NullLogger<EmbeddingService>.Instance);

    private Document ClassifiedDoc(Guid id) => new()
    {
        Id = id,
        FileName = "doc.txt",
        ContentType = "text/plain",
        FilePath = "2026/05/31/key.txt",
        SizeBytes = 100,
        Status = DocumentStatus.Classified,
        UploadedAt = FixedNow.UtcDateTime,
        ProcessedAt = FixedNow.UtcDateTime,
    };

    // Content long enough (with the 40-char budget) to produce multiple chunks.
    private const string MultiChunkText =
        "First sentence about invoices and amounts. Second sentence about totals due. Third about dates.";

    private void SetupClaimable(Document doc, bool claimWins = true, string content = MultiChunkText)
    {
        _documents.Setup(r => r.GetByIdAsync(doc.Id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);
        _documents.Setup(r => r.TryClaimForEmbeddingAsync(doc.Id, It.IsAny<CancellationToken>())).ReturnsAsync(claimWins);
        _texts.Setup(t => t.GetByDocumentIdAsync(doc.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentText { Id = Guid.CreateVersion7(), DocumentId = doc.Id, Content = content, CharCount = content.Length, ExtractedAt = FixedNow.UtcDateTime });
    }

    [Fact]
    public async Task EmbedDocumentAsync_HappyPath_UpsertsQdrantThenPersistsChunksAndReadyForSearch()
    {
        var id = Guid.CreateVersion7();
        var doc = ClassifiedDoc(id);
        SetupClaimable(doc);

        IReadOnlyList<DocumentChunk>? savedChunks = null;
        _chunks.Setup(r => r.ReplaceForDocumentAsync(id, It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyList<DocumentChunk>, CancellationToken>((_, c, _) => savedChunks = c).Returns(Task.CompletedTask);

        List<ChunkVector>? upserted = null;
        _vectorStore.Setup(v => v.UpsertChunksAsync(It.IsAny<IReadOnlyList<ChunkVector>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChunkVector>, CancellationToken>((v, _) => upserted = v.ToList()).Returns(Task.CompletedTask);

        var actions = new List<string>();
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => actions.Add(a.Action)).Returns(Task.CompletedTask);

        var outcome = await CreateSut().EmbedDocumentAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Succeeded);
        doc.Status.Should().Be(DocumentStatus.ReadyForSearch);
        doc.ProcessedAt.Should().Be(FixedNow.UtcDateTime);
        doc.FailureReason.Should().BeNull();

        savedChunks.Should().NotBeNull();
        savedChunks!.Count.Should().BeGreaterThan(1);
        // Gap-free 0-based indices, PointId set, deterministic from (documentId, chunkIndex).
        for (var i = 0; i < savedChunks.Count; i++)
        {
            savedChunks[i].ChunkIndex.Should().Be(i);
            savedChunks[i].PointId.Should().Be(DeterministicPointId.For(id, i));
            savedChunks[i].Content.Should().NotBeNullOrEmpty();
        }

        upserted.Should().NotBeNull();
        upserted!.Count.Should().Be(savedChunks.Count);
        upserted.Select(v => v.PointId).Should().Equal(savedChunks.Select(c => c.PointId));

        actions.Should().ContainInOrder("EmbeddingStarted", "EmbeddingSucceeded");
    }

    [Fact]
    public async Task EmbedDocumentAsync_QdrantWrittenBeforeSqlPersist()
    {
        var id = Guid.CreateVersion7();
        var doc = ClassifiedDoc(id);
        SetupClaimable(doc);

        var order = new List<string>();
        _vectorStore.Setup(v => v.DeleteByDocumentAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("qdrant-delete")).Returns(Task.CompletedTask);
        _vectorStore.Setup(v => v.UpsertChunksAsync(It.IsAny<IReadOnlyList<ChunkVector>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("qdrant-upsert")).Returns(Task.CompletedTask);
        _chunks.Setup(r => r.ReplaceForDocumentAsync(id, It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("sql-persist")).Returns(Task.CompletedTask);

        await CreateSut().EmbedDocumentAsync(id, CancellationToken.None);

        // Dual-store ordering invariant: Qdrant delete → upsert → SQL persist (ADR §6).
        order.Should().Equal("qdrant-delete", "qdrant-upsert", "sql-persist");
    }

    [Fact]
    public async Task EmbedDocumentAsync_EmbedderDown_StaysClassifiedReturnsTransientWritesNothing()
    {
        var id = Guid.CreateVersion7();
        var doc = ClassifiedDoc(id);
        SetupClaimable(doc);
        _embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmbeddingUnavailableException("embedder unreachable"));

        var outcome = await CreateSut().EmbedDocumentAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Transient);
        doc.Status.Should().Be(DocumentStatus.Classified); // claim rolled back, NOT Failed
        doc.FailureReason.Should().BeNull();
        _vectorStore.Verify(v => v.UpsertChunksAsync(It.IsAny<IReadOnlyList<ChunkVector>>(), It.IsAny<CancellationToken>()), Times.Never);
        _chunks.Verify(r => r.ReplaceForDocumentAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmbedDocumentAsync_QdrantDown_StaysClassifiedReturnsTransientNoSqlPersist()
    {
        var id = Guid.CreateVersion7();
        var doc = ClassifiedDoc(id);
        SetupClaimable(doc);
        _vectorStore.Setup(v => v.UpsertChunksAsync(It.IsAny<IReadOnlyList<ChunkVector>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VectorStoreUnavailableException("qdrant unreachable"));

        var outcome = await CreateSut().EmbedDocumentAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Transient);
        doc.Status.Should().Be(DocumentStatus.Classified);
        _chunks.Verify(r => r.ReplaceForDocumentAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmbedDocumentAsync_PartialEmbedThenFault_WritesNothingToEitherStore()
    {
        var id = Guid.CreateVersion7();
        var doc = ClassifiedDoc(id);
        SetupClaimable(doc);
        // First chunk embeds, second chunk's embedding fails after retries → transient, nothing written.
        _embedder.SetupSequence(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingResult(new float[768], "nomic-embed-text", TimeSpan.Zero))
            .ThrowsAsync(new EmbeddingUnavailableException("dropped mid-loop"))
            .ThrowsAsync(new EmbeddingUnavailableException("dropped mid-loop"));

        var outcome = await CreateSut(new EmbeddingOptions { MaxAttempts = 2 }).EmbedDocumentAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Transient);
        doc.Status.Should().Be(DocumentStatus.Classified);
        _vectorStore.Verify(v => v.DeleteByDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _vectorStore.Verify(v => v.UpsertChunksAsync(It.IsAny<IReadOnlyList<ChunkVector>>(), It.IsAny<CancellationToken>()), Times.Never);
        _chunks.Verify(r => r.ReplaceForDocumentAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmbedDocumentAsync_ReEmbed_DeletesFirstAndUsesDeterministicIds()
    {
        var id = Guid.CreateVersion7();
        var doc = ClassifiedDoc(id);
        SetupClaimable(doc);

        // First embed run.
        List<ChunkVector>? firstRun = null;
        _vectorStore.Setup(v => v.UpsertChunksAsync(It.IsAny<IReadOnlyList<ChunkVector>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChunkVector>, CancellationToken>((v, _) => firstRun = v.ToList()).Returns(Task.CompletedTask);
        await CreateSut().EmbedDocumentAsync(id, CancellationToken.None);

        // Reset doc to Classified and re-run (re-process).
        doc.Status = DocumentStatus.Classified;
        List<ChunkVector>? secondRun = null;
        _vectorStore.Setup(v => v.UpsertChunksAsync(It.IsAny<IReadOnlyList<ChunkVector>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChunkVector>, CancellationToken>((v, _) => secondRun = v.ToList()).Returns(Task.CompletedTask);
        await CreateSut().EmbedDocumentAsync(id, CancellationToken.None);

        // Delete-by-document called on each run (delete-before-write idempotency, ADR §6).
        _vectorStore.Verify(v => v.DeleteByDocumentAsync(id, It.IsAny<CancellationToken>()), Times.Exactly(2));
        // Deterministic point ids: the same (documentId, chunkIndex) yields the same PointId across runs.
        firstRun!.Select(v => v.PointId).Should().Equal(secondRun!.Select(v => v.PointId));
    }

    [Fact]
    public async Task EmbedDocumentAsync_ClaimLost_ReturnsNotClaimedAndDoesNotEmbed()
    {
        var id = Guid.CreateVersion7();
        var doc = ClassifiedDoc(id);
        SetupClaimable(doc, claimWins: false);

        var outcome = await CreateSut().EmbedDocumentAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.NotClaimed);
        _embedder.Verify(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmbedDocumentAsync_MissingDocument_ReturnsNotFound()
    {
        var id = Guid.CreateVersion7();
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Document?)null);

        var outcome = await CreateSut().EmbedDocumentAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.NotFound);
    }

    [Fact]
    public async Task EmbedDocumentAsync_NoExtractedText_FailsAsContentFault()
    {
        var id = Guid.CreateVersion7();
        var doc = ClassifiedDoc(id);
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);
        _documents.Setup(r => r.TryClaimForEmbeddingAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _texts.Setup(t => t.GetByDocumentIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((DocumentText?)null);

        var outcome = await CreateSut().EmbedDocumentAsync(id, CancellationToken.None);

        outcome.Should().Be(ProcessingOutcome.Failed);
        doc.Status.Should().Be(DocumentStatus.Failed);
        _embedder.Verify(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecoverStaleGeneratingEmbeddings_StaleDoc_ResetsToClassified()
    {
        var id = Guid.CreateVersion7();
        var doc = ClassifiedDoc(id);
        doc.Status = DocumentStatus.GeneratingEmbeddings;

        _documents.Setup(r => r.GetStaleGeneratingEmbeddingsIdsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id });
        _documents.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);

        AuditLog? written = null;
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((a, _) => written = a).Returns(Task.CompletedTask);

        var reset = await CreateSut().RecoverStaleGeneratingEmbeddingsAsync(TimeSpan.FromMinutes(15), CancellationToken.None);

        reset.Should().Be(1);
        doc.Status.Should().Be(DocumentStatus.Classified);
        written!.Action.Should().Be(nameof(AuditAction.ReprocessQueued));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
