using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Search;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DocuPilot.UnitTests.Search;

/// <summary>
/// Unit tests for <see cref="SearchService"/> — the Phase-6 semantic search orchestrator (DA-045).
/// The <see cref="IEmbeddingClient"/> and <see cref="IVectorStore"/> are STUBBED (no real
/// Ollama/Qdrant) and the repositories are mocked. Covers: happy path (chunk hits → grouped
/// best-per-doc → ranked → hydrated DTOs); dedup/grouping (multiple chunks of one doc → one result,
/// best score); limit clamp + over-fetch sizing; MinScore filtering; category filter; matchedText
/// from SQL Content (trimmed) + Qdrant-snippet fallback; empty query → EmptyQuery (400 mapping);
/// embedder-down → Unavailable (503 mapping); Qdrant-down → Unavailable (503 mapping); no-results →
/// empty list (200 mapping).
/// </summary>
public sealed class SearchServiceTests
{
    private readonly Mock<IEmbeddingClient> _embedder = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IDocumentRepository> _documents = new();
    private readonly Mock<IDocumentChunkRepository> _chunks = new();
    private readonly Mock<IDocumentClassificationRepository> _classifications = new();

    public SearchServiceTests()
    {
        _embedder.SetupGet(e => e.Dimensions).Returns(768);
        _embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingResult(new float[768], "nomic-embed-text", TimeSpan.Zero));
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ChunkHit>)[]);
        _documents.Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Document>)[]);
        _chunks.Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DocumentChunk>)[]);
        _classifications.Setup(r => r.GetByDocumentIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DocumentClassification>)[]);
    }

    private SearchService CreateSut(SearchOptions? options = null) => new(
        _embedder.Object,
        _vectorStore.Object,
        _documents.Object,
        _chunks.Object,
        _classifications.Object,
        Options.Create(options ?? new SearchOptions()),
        NullLogger<SearchService>.Instance);

    private static ChunkHit Hit(Guid docId, Guid chunkId, int chunkIndex, float score, string? snippet = null) =>
        new(Guid.CreateVersion7(), docId, chunkId, chunkIndex, score, snippet);

    private static Document Doc(Guid id, string fileName) => new()
    {
        Id = id,
        FileName = fileName,
        ContentType = "text/plain",
        FilePath = "k.txt",
        SizeBytes = 1,
        Status = DocumentStatus.ReadyForSearch,
        UploadedAt = DateTime.UtcNow,
    };

    private static DocumentChunk Chunk(Guid id, Guid docId, int index, string content) => new()
    {
        Id = id,
        DocumentId = docId,
        ChunkIndex = index,
        Content = content,
        TokenEstimate = content.Length / 4,
        PointId = Guid.CreateVersion7(),
        CreatedAt = DateTime.UtcNow,
    };

    private static DocumentClassification Classification(Guid docId, DocumentCategory category) => new()
    {
        Id = Guid.CreateVersion7(),
        DocumentId = docId,
        Classification = category,
        Confidence = 0.9m,
        CreatedAt = DateTime.UtcNow,
    };

    private void SetupHits(params ChunkHit[] hits) =>
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hits);

    private void SetupDocs(params Document[] docs) =>
        _documents.Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(docs);

    private void SetupChunks(params DocumentChunk[] chunks) =>
        _chunks.Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);

    private void SetupClassifications(params DocumentClassification[] classifications) =>
        _classifications.Setup(r => r.GetByDocumentIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(classifications);

    [Fact]
    public async Task SearchAsync_HappyPath_RanksDocsByScoreDesc_AndHydratesDtos()
    {
        var docA = Guid.CreateVersion7();
        var docB = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        var chunkB = Guid.CreateVersion7();

        SetupHits(Hit(docB, chunkB, 0, 0.70f), Hit(docA, chunkA, 2, 0.92f));
        SetupDocs(Doc(docA, "a.pdf"), Doc(docB, "b.pdf"));
        SetupChunks(Chunk(chunkA, docA, 2, "Alpha matched passage."), Chunk(chunkB, docB, 0, "Beta matched passage."));
        SetupClassifications(Classification(docA, DocumentCategory.Contract), Classification(docB, DocumentCategory.Invoice));

        var outcome = await CreateSut().SearchAsync(new SearchQuery("find things"), CancellationToken.None);

        outcome.Kind.Should().Be(SearchOutcomeKind.Results);
        outcome.Results.Should().HaveCount(2);

        // Ordered by score descending: docA (0.92) then docB (0.70).
        outcome.Results[0].DocumentId.Should().Be(docA);
        outcome.Results[0].FileName.Should().Be("a.pdf");
        outcome.Results[0].Classification.Should().Be("Contract");
        outcome.Results[0].Score.Should().Be(0.92f);
        outcome.Results[0].MatchedText.Should().Be("Alpha matched passage.");
        outcome.Results[0].ChunkIndex.Should().Be(2);

        outcome.Results[1].DocumentId.Should().Be(docB);
        outcome.Results[1].Score.Should().Be(0.70f);
    }

    [Fact]
    public async Task SearchAsync_MultipleChunksSameDoc_CollapseToOneResult_WithBestScore()
    {
        var docA = Guid.CreateVersion7();
        var bestChunk = Guid.CreateVersion7();
        var worseChunk = Guid.CreateVersion7();

        // Same document, three chunks, varying scores. Expect ONE result with the max score (0.88)
        // and the winning chunk's index/content.
        SetupHits(
            Hit(docA, worseChunk, 0, 0.40f),
            Hit(docA, bestChunk, 5, 0.88f),
            Hit(docA, Guid.CreateVersion7(), 2, 0.55f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(bestChunk, docA, 5, "Best chunk."), Chunk(worseChunk, docA, 0, "Worse chunk."));
        SetupClassifications(Classification(docA, DocumentCategory.Contract));

        var outcome = await CreateSut().SearchAsync(new SearchQuery("q"), CancellationToken.None);

        outcome.Results.Should().HaveCount(1);
        outcome.Results[0].DocumentId.Should().Be(docA);
        outcome.Results[0].Score.Should().Be(0.88f);
        outcome.Results[0].ChunkIndex.Should().Be(5);
        outcome.Results[0].MatchedText.Should().Be("Best chunk.");
    }

    [Fact]
    public async Task SearchAsync_LimitClampedToMax_AndOverFetchSizingApplied()
    {
        int? capturedOverFetch = null;
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<float[], int, Guid?, CancellationToken>((_, limit, _, _) => capturedOverFetch = limit)
            .ReturnsAsync((IReadOnlyList<ChunkHit>)[]);

        var options = new SearchOptions { MaxLimit = 50, ChunkOverFetchFactor = 5, MaxChunkFetch = 200 };

        // Request 999 → clamped to MaxLimit 50 → over-fetch 50 × 5 = 250 → capped at MaxChunkFetch 200.
        await CreateSut(options).SearchAsync(new SearchQuery("q", Limit: 999), CancellationToken.None);

        capturedOverFetch.Should().Be(200);
    }

    [Fact]
    public async Task SearchAsync_ResultCountNeverExceedsClampedLimit()
    {
        var hits = Enumerable.Range(0, 30)
            .Select(i => Hit(Guid.CreateVersion7(), Guid.CreateVersion7(), 0, 0.9f - (i * 0.01f)))
            .ToArray();
        SetupHits(hits);
        SetupDocs(hits.Select(h => Doc(h.DocumentId, "f.pdf")).ToArray());
        SetupChunks(hits.Select(h => Chunk(h.ChunkId, h.DocumentId, 0, "text")).ToArray());

        var outcome = await CreateSut(new SearchOptions { DefaultLimit = 10, MaxLimit = 50 })
            .SearchAsync(new SearchQuery("q"), CancellationToken.None);

        // 30 distinct docs available, but DefaultLimit is 10.
        outcome.Results.Should().HaveCount(10);
    }

    [Fact]
    public async Task SearchAsync_MinScoreSet_DropsHitsBelowFloor()
    {
        var docHigh = Guid.CreateVersion7();
        var docLow = Guid.CreateVersion7();
        var chunkHigh = Guid.CreateVersion7();
        var chunkLow = Guid.CreateVersion7();

        SetupHits(Hit(docHigh, chunkHigh, 0, 0.80f), Hit(docLow, chunkLow, 0, 0.20f));
        SetupDocs(Doc(docHigh, "high.pdf"), Doc(docLow, "low.pdf"));
        SetupChunks(Chunk(chunkHigh, docHigh, 0, "high"), Chunk(chunkLow, docLow, 0, "low"));

        var outcome = await CreateSut(new SearchOptions { MinScore = 0.5 })
            .SearchAsync(new SearchQuery("q"), CancellationToken.None);

        outcome.Results.Should().HaveCount(1);
        outcome.Results[0].DocumentId.Should().Be(docHigh);
    }

    [Fact]
    public async Task SearchAsync_CategoryFilter_RestrictsToMatchingClassification()
    {
        var contractDoc = Guid.CreateVersion7();
        var invoiceDoc = Guid.CreateVersion7();
        var contractChunk = Guid.CreateVersion7();
        var invoiceChunk = Guid.CreateVersion7();

        SetupHits(Hit(contractDoc, contractChunk, 0, 0.90f), Hit(invoiceDoc, invoiceChunk, 0, 0.85f));
        SetupDocs(Doc(contractDoc, "c.pdf"), Doc(invoiceDoc, "i.pdf"));
        SetupChunks(Chunk(contractChunk, contractDoc, 0, "contract text"), Chunk(invoiceChunk, invoiceDoc, 0, "invoice text"));
        SetupClassifications(
            Classification(contractDoc, DocumentCategory.Contract),
            Classification(invoiceDoc, DocumentCategory.Invoice));

        var outcome = await CreateSut().SearchAsync(new SearchQuery("q", Category: "Contract"), CancellationToken.None);

        outcome.Results.Should().HaveCount(1);
        outcome.Results[0].DocumentId.Should().Be(contractDoc);
        outcome.Results[0].Classification.Should().Be("Contract");
    }

    [Fact]
    public async Task SearchAsync_UnknownCategory_IgnoredGracefully_ReturnsAll()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        SetupHits(Hit(docA, chunkA, 0, 0.90f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkA, docA, 0, "text"));
        SetupClassifications(Classification(docA, DocumentCategory.Contract));

        // "NotARealCategory" is off-taxonomy → ignored gracefully (treated as no filter, PM Q5), so
        // the Contract doc is still returned rather than the request crashing or returning nothing.
        var outcome = await CreateSut().SearchAsync(new SearchQuery("q", Category: "NotARealCategory"), CancellationToken.None);

        outcome.Kind.Should().Be(SearchOutcomeKind.Results);
        outcome.Results.Should().HaveCount(1);
        outcome.Results[0].DocumentId.Should().Be(docA);
    }

    [Fact]
    public async Task SearchAsync_BlankCategory_TreatedAsNoFilter()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        SetupHits(Hit(docA, chunkA, 0, 0.90f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkA, docA, 0, "text"));
        SetupClassifications(Classification(docA, DocumentCategory.Contract));

        var outcome = await CreateSut().SearchAsync(new SearchQuery("q", Category: "   "), CancellationToken.None);

        outcome.Results.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchAsync_MatchedText_TrimmedToConfiguredBudget()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        var longContent = new string('x', 1000);
        SetupHits(Hit(docA, chunkA, 0, 0.90f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkA, docA, 0, longContent));

        var outcome = await CreateSut(new SearchOptions { MatchedTextMaxChars = 300 })
            .SearchAsync(new SearchQuery("q"), CancellationToken.None);

        outcome.Results[0].MatchedText.Should().HaveLength(300);
    }

    [Fact]
    public async Task SearchAsync_ChunkRowMissing_FallsBackToQdrantSnippet()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        SetupHits(Hit(docA, chunkA, 0, 0.90f, snippet: "snippet fallback"));
        SetupDocs(Doc(docA, "a.pdf"));
        // No chunk row returned → SQL Content missing → use the Qdrant snippet.
        SetupChunks();

        var outcome = await CreateSut().SearchAsync(new SearchQuery("q"), CancellationToken.None);

        outcome.Results.Should().HaveCount(1);
        outcome.Results[0].MatchedText.Should().Be("snippet fallback");
    }

    [Fact]
    public async Task SearchAsync_DocumentRowMissing_SkipsThatHit_DriftGuard()
    {
        var docPresent = Guid.CreateVersion7();
        var docGone = Guid.CreateVersion7();
        var chunkPresent = Guid.CreateVersion7();
        var chunkGone = Guid.CreateVersion7();

        SetupHits(Hit(docGone, chunkGone, 0, 0.95f), Hit(docPresent, chunkPresent, 0, 0.80f));
        // Only docPresent's row exists in SQL (docGone drifted out).
        SetupDocs(Doc(docPresent, "present.pdf"));
        SetupChunks(Chunk(chunkPresent, docPresent, 0, "present text"));

        var outcome = await CreateSut().SearchAsync(new SearchQuery("q"), CancellationToken.None);

        outcome.Results.Should().HaveCount(1);
        outcome.Results[0].DocumentId.Should().Be(docPresent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SearchAsync_EmptyOrWhitespaceQuery_ReturnsEmptyQuery(string? query)
    {
        var outcome = await CreateSut().SearchAsync(new SearchQuery(query!), CancellationToken.None);

        outcome.Kind.Should().Be(SearchOutcomeKind.EmptyQuery);
        // No embedding / search attempted for a bad query.
        _embedder.Verify(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _vectorStore.Verify(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_EmbedderDown_ReturnsUnavailable()
    {
        _embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmbeddingUnavailableException("ollama down"));

        var outcome = await CreateSut().SearchAsync(new SearchQuery("q"), CancellationToken.None);

        outcome.Kind.Should().Be(SearchOutcomeKind.Unavailable);
    }

    [Fact]
    public async Task SearchAsync_QdrantDown_ReturnsUnavailable()
    {
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VectorStoreUnavailableException("qdrant down"));

        var outcome = await CreateSut().SearchAsync(new SearchQuery("q"), CancellationToken.None);

        outcome.Kind.Should().Be(SearchOutcomeKind.Unavailable);
    }

    [Fact]
    public async Task SearchAsync_NoHits_ReturnsEmptyResults()
    {
        // Default setup: SearchAsync returns []. No SQL hydration round-trips needed.
        var outcome = await CreateSut().SearchAsync(new SearchQuery("nothing matches"), CancellationToken.None);

        outcome.Kind.Should().Be(SearchOutcomeKind.Results);
        outcome.Results.Should().BeEmpty();
        _documents.Verify(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_QueriesAcrossAllDocuments_NotScopedToOne()
    {
        Guid? capturedDocScope = Guid.CreateVersion7();
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<float[], int, Guid?, CancellationToken>((_, _, docId, _) => capturedDocScope = docId)
            .ReturnsAsync((IReadOnlyList<ChunkHit>)[]);

        await CreateSut().SearchAsync(new SearchQuery("q"), CancellationToken.None);

        // Global search: documentId scope is null (ADR §3 — documentId-scope deferred to Phase 7).
        capturedDocScope.Should().BeNull();
    }
}
