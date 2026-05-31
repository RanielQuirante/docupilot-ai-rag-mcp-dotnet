using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using DocuPilot.Services.Rag;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DocuPilot.UnitTests.Rag;

/// <summary>
/// Unit tests for <see cref="RagService"/> — the Phase-7 RAG question-answering orchestrator (DA-049).
/// The <see cref="IEmbeddingClient"/>, <see cref="IVectorStore"/>, and <see cref="ILlmClient"/> are
/// STUBBED (no real Ollama/Qdrant) and the repositories are mocked. Covers: happy path (hits → context
/// built → LLM prose answer → citations = ALL retrieved chunks ranked by score); the grounding
/// short-circuit (zero hits / all-below-MinScore → canned not-found, answerFound=false, empty
/// citations, LLM NOT called); post-LLM canned-phrase detection; topK clamp; context-budget truncation
/// (over-budget drops lowest-score chunks from context but they still cite); category filter; Page
/// always null; the verbatim grounding system prompt + JsonMode=false + Temperature=0 on the LLM call;
/// empty question → EmptyQuestion (400); embedder/Qdrant/LLM-down → Unavailable (503).
/// </summary>
public sealed class RagServiceTests
{
    private const string CannedNotFound = "I could not find enough information in the uploaded documents.";
    private const string GroundingSystem =
        "Answer only using the provided document context. "
        + "If the answer is not found in the context, say: "
        + "\"I could not find enough information in the uploaded documents.\"";

    private readonly Mock<IEmbeddingClient> _embedder = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<ILlmClient> _llm = new();
    private readonly Mock<IPromptProvider> _prompts = new();
    private readonly Mock<IDocumentRepository> _documents = new();
    private readonly Mock<IDocumentChunkRepository> _chunks = new();
    private readonly Mock<IDocumentClassificationRepository> _classifications = new();

    public RagServiceTests()
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

        // Default LLM: echoes a real prose answer (the happy path). Tests override as needed.
        _llm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse("The contract expires on 2026-09-01.", "llama3.2:3b", TimeSpan.Zero));

        // Prompt provider stub: real grounding contract strings + a trivial prompt builder.
        _prompts.SetupGet(p => p.RagGroundingSystemPrompt).Returns(GroundingSystem);
        _prompts.SetupGet(p => p.RagNotFoundAnswer).Returns(CannedNotFound);
        _prompts.Setup(p => p.BuildRagPrompt(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((q, ctx) => $"Q:{q}\nCTX:{ctx}");
    }

    private RagService CreateSut(RagOptions? options = null) => new(
        _embedder.Object,
        _vectorStore.Object,
        _llm.Object,
        _prompts.Object,
        _documents.Object,
        _chunks.Object,
        _classifications.Object,
        Options.Create(options ?? new RagOptions()),
        Options.Create(new LlmOptions()),
        NullLogger<RagService>.Instance);

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

    private void SetupLlmAnswer(string answer) =>
        _llm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse(answer, "llama3.2:3b", TimeSpan.Zero));

    [Fact]
    public async Task AskAsync_HappyPath_BuildsContext_CallsLlm_AnswerFound_CitesAllRetrievedChunksByScore()
    {
        var docA = Guid.CreateVersion7();
        var docB = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        var chunkB = Guid.CreateVersion7();

        SetupHits(Hit(docB, chunkB, 0, 0.70f), Hit(docA, chunkA, 2, 0.92f));
        SetupDocs(Doc(docA, "a.pdf"), Doc(docB, "b.pdf"));
        SetupChunks(Chunk(chunkA, docA, 2, "Alpha passage."), Chunk(chunkB, docB, 0, "Beta passage."));
        SetupLlmAnswer("Grounded prose answer.");

        var outcome = await CreateSut().AskAsync(new AskQuery("when does it expire?"), CancellationToken.None);

        outcome.Kind.Should().Be(AskOutcomeKind.Answer);
        outcome.Result!.AnswerFound.Should().BeTrue();
        outcome.Result.Answer.Should().Be("Grounded prose answer.");

        // Citations = ALL retrieved chunks, ranked by score descending (docA 0.92 first, docB 0.70).
        outcome.Result.Citations.Should().HaveCount(2);
        outcome.Result.Citations[0].DocumentId.Should().Be(docA);
        outcome.Result.Citations[0].FileName.Should().Be("a.pdf");
        outcome.Result.Citations[0].ChunkIndex.Should().Be(2);
        outcome.Result.Citations[0].Score.Should().Be(0.92f);
        outcome.Result.Citations[0].Snippet.Should().Be("Alpha passage.");
        outcome.Result.Citations[0].Page.Should().BeNull();
        outcome.Result.Citations[1].DocumentId.Should().Be(docB);
        outcome.Result.Citations[1].Score.Should().Be(0.70f);
    }

    [Fact]
    public async Task AskAsync_CallsLlm_WithVerbatimGroundingSystem_ProseMode_TempZero()
    {
        LlmRequest? captured = null;
        _llm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new LlmResponse("answer", "llama3.2:3b", TimeSpan.Zero));

        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        SetupHits(Hit(docA, chunkA, 0, 0.9f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkA, docA, 0, "passage"));

        await CreateSut().AskAsync(new AskQuery("q"), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.System.Should().Be(GroundingSystem);
        captured.JsonMode.Should().BeFalse();
        captured.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task AskAsync_ZeroHits_ShortCircuits_NotFound_LlmNotCalled()
    {
        // Default setup: SearchAsync returns []. Expect the canned not-found WITHOUT touching the LLM.
        var outcome = await CreateSut().AskAsync(new AskQuery("anything"), CancellationToken.None);

        outcome.Kind.Should().Be(AskOutcomeKind.Answer);
        outcome.Result!.AnswerFound.Should().BeFalse();
        outcome.Result.Answer.Should().Be(CannedNotFound);
        outcome.Result.Citations.Should().BeEmpty();

        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        // No hydration round-trips either — short-circuit happens before hydration.
        _documents.Verify(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AskAsync_AllHitsBelowMinScore_ShortCircuits_NotFound_LlmNotCalled()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        // Hit exists but below the default MinScore (0.25).
        SetupHits(Hit(docA, chunkA, 0, 0.10f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkA, docA, 0, "irrelevant"));

        var outcome = await CreateSut().AskAsync(new AskQuery("off-topic question"), CancellationToken.None);

        outcome.Result!.AnswerFound.Should().BeFalse();
        outcome.Result.Answer.Should().Be(CannedNotFound);
        outcome.Result.Citations.Should().BeEmpty();
        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AskAsync_AtLeastOneHitAboveMinScore_DoesNotShortCircuit_CallsLlm()
    {
        var docA = Guid.CreateVersion7();
        var chunkLow = Guid.CreateVersion7();
        var chunkHigh = Guid.CreateVersion7();
        // One below, one above the floor → the set is NOT all-below → proceed.
        SetupHits(Hit(docA, chunkLow, 0, 0.10f), Hit(docA, chunkHigh, 1, 0.40f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkLow, docA, 0, "low"), Chunk(chunkHigh, docA, 1, "high"));
        SetupLlmAnswer("real answer");

        var outcome = await CreateSut().AskAsync(new AskQuery("q"), CancellationToken.None);

        outcome.Result!.AnswerFound.Should().BeTrue();
        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AskAsync_ModelReturnsCannedPhrase_PostLlmDetection_AnswerFoundFalse_EmptyCitations()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        SetupHits(Hit(docA, chunkA, 0, 0.9f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkA, docA, 0, "passage"));
        // Model itself emits the canned phrase (with surrounding prose) → not-found, empty citations.
        SetupLlmAnswer("I could not find enough information in the uploaded documents.");

        var outcome = await CreateSut().AskAsync(new AskQuery("q"), CancellationToken.None);

        outcome.Result!.AnswerFound.Should().BeFalse();
        outcome.Result.Answer.Should().Be(CannedNotFound);
        outcome.Result.Citations.Should().BeEmpty();
    }

    [Fact]
    public async Task AskAsync_TopKClampedToMax()
    {
        int? capturedTopK = null;
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<float[], int, Guid?, CancellationToken>((_, k, _, _) => capturedTopK = k)
            .ReturnsAsync((IReadOnlyList<ChunkHit>)[]);

        // Request 999 → clamped to MaxTopK 12.
        await CreateSut(new RagOptions { TopK = 6, MaxTopK = 12 })
            .AskAsync(new AskQuery("q", TopK: 999), CancellationToken.None);

        capturedTopK.Should().Be(12);
    }

    [Fact]
    public async Task AskAsync_TopKDefaultsWhenOmitted()
    {
        int? capturedTopK = null;
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<float[], int, Guid?, CancellationToken>((_, k, _, _) => capturedTopK = k)
            .ReturnsAsync((IReadOnlyList<ChunkHit>)[]);

        await CreateSut(new RagOptions { TopK = 6, MaxTopK = 12 })
            .AskAsync(new AskQuery("q"), CancellationToken.None);

        capturedTopK.Should().Be(6);
    }

    [Fact]
    public async Task AskAsync_ContextBudget_DropsLowestScoreChunksFromContext_ButTheyStillCite()
    {
        var docA = Guid.CreateVersion7();
        var chunkHigh = Guid.CreateVersion7();
        var chunkLow = Guid.CreateVersion7();

        // Two big chunks; a tiny context budget that only fits the first (top-score) one.
        var bigHigh = new string('H', 500);
        var bigLow = new string('L', 500);
        SetupHits(Hit(docA, chunkHigh, 0, 0.95f), Hit(docA, chunkLow, 1, 0.50f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkHigh, docA, 0, bigHigh), Chunk(chunkLow, docA, 1, bigLow));

        string capturedContext = string.Empty;
        _prompts.Setup(p => p.BuildRagPrompt(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, ctx) => capturedContext = ctx)
            .Returns("prompt");
        SetupLlmAnswer("answer");

        // ContextMaxChars small enough that only the top chunk fits; PerChunkMaxChars large enough not to clip.
        var outcome = await CreateSut(new RagOptions { ContextMaxChars = 600, PerChunkMaxChars = 2000, SnippetMaxChars = 1000 })
            .AskAsync(new AskQuery("q"), CancellationToken.None);

        // Context only contains the high-score chunk's body (the low one was over budget → dropped).
        capturedContext.Should().Contain(bigHigh);
        capturedContext.Should().NotContain(bigLow);

        // But BOTH chunks still appear as citations (retrieved/relevant — we just couldn't fit the text).
        outcome.Result!.Citations.Should().HaveCount(2);
        outcome.Result.Citations.Select(c => c.ChunkIndex).Should().BeEquivalentTo([0, 1]);
    }

    [Fact]
    public async Task AskAsync_PerChunkCap_TrimsHugeChunkBeforeAssembly()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        var huge = new string('x', 5000);
        SetupHits(Hit(docA, chunkA, 0, 0.9f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkA, docA, 0, huge));

        string capturedContext = string.Empty;
        _prompts.Setup(p => p.BuildRagPrompt(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, ctx) => capturedContext = ctx)
            .Returns("prompt");
        SetupLlmAnswer("answer");

        await CreateSut(new RagOptions { PerChunkMaxChars = 100, ContextMaxChars = 6000 })
            .AskAsync(new AskQuery("q"), CancellationToken.None);

        // The chunk body in the context is capped at PerChunkMaxChars (100), not the full 5000.
        capturedContext.Should().NotContain(new string('x', 101));
        capturedContext.Should().Contain(new string('x', 100));
    }

    [Fact]
    public async Task AskAsync_SnippetTrimmedToConfiguredBudget()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        var longContent = new string('y', 1000);
        SetupHits(Hit(docA, chunkA, 0, 0.9f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkA, docA, 0, longContent));
        SetupLlmAnswer("answer");

        var outcome = await CreateSut(new RagOptions { SnippetMaxChars = 300 })
            .AskAsync(new AskQuery("q"), CancellationToken.None);

        outcome.Result!.Citations[0].Snippet.Should().HaveLength(300);
    }

    [Fact]
    public async Task AskAsync_CategoryFilter_RestrictsContextAndCitationsToMatchingClassification()
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
        SetupLlmAnswer("answer");

        var outcome = await CreateSut().AskAsync(new AskQuery("q", Category: "Contract"), CancellationToken.None);

        outcome.Result!.AnswerFound.Should().BeTrue();
        outcome.Result.Citations.Should().HaveCount(1);
        outcome.Result.Citations[0].DocumentId.Should().Be(contractDoc);
    }

    [Fact]
    public async Task AskAsync_CategoryFilterEmptiesSet_ShortCircuitsNotFound()
    {
        var invoiceDoc = Guid.CreateVersion7();
        var invoiceChunk = Guid.CreateVersion7();
        SetupHits(Hit(invoiceDoc, invoiceChunk, 0, 0.90f));
        SetupDocs(Doc(invoiceDoc, "i.pdf"));
        SetupChunks(Chunk(invoiceChunk, invoiceDoc, 0, "invoice text"));
        SetupClassifications(Classification(invoiceDoc, DocumentCategory.Invoice));

        // Filter for Contract but only an Invoice chunk was retrieved → set empties → not-found, no LLM.
        var outcome = await CreateSut().AskAsync(new AskQuery("q", Category: "Contract"), CancellationToken.None);

        outcome.Result!.AnswerFound.Should().BeFalse();
        outcome.Result.Answer.Should().Be(CannedNotFound);
        outcome.Result.Citations.Should().BeEmpty();
        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AskAsync_DocumentRowMissing_SkipsThatHit_DriftGuard()
    {
        var docPresent = Guid.CreateVersion7();
        var docGone = Guid.CreateVersion7();
        var chunkPresent = Guid.CreateVersion7();
        var chunkGone = Guid.CreateVersion7();

        SetupHits(Hit(docGone, chunkGone, 0, 0.95f), Hit(docPresent, chunkPresent, 0, 0.80f));
        SetupDocs(Doc(docPresent, "present.pdf"));
        SetupChunks(Chunk(chunkPresent, docPresent, 0, "present text"));
        SetupLlmAnswer("answer");

        var outcome = await CreateSut().AskAsync(new AskQuery("q"), CancellationToken.None);

        outcome.Result!.Citations.Should().HaveCount(1);
        outcome.Result.Citations[0].DocumentId.Should().Be(docPresent);
    }

    [Fact]
    public async Task AskAsync_ChunkRowMissing_FallsBackToQdrantSnippet()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        SetupHits(Hit(docA, chunkA, 0, 0.90f, snippet: "snippet fallback"));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(); // no chunk row → use the Qdrant snippet
        SetupLlmAnswer("answer");

        var outcome = await CreateSut().AskAsync(new AskQuery("q"), CancellationToken.None);

        outcome.Result!.Citations.Should().HaveCount(1);
        outcome.Result.Citations[0].Snippet.Should().Be("snippet fallback");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AskAsync_EmptyOrWhitespaceQuestion_ReturnsEmptyQuestion(string? question)
    {
        var outcome = await CreateSut().AskAsync(new AskQuery(question!), CancellationToken.None);

        outcome.Kind.Should().Be(AskOutcomeKind.EmptyQuestion);
        // No embedding / search / LLM attempted for a bad question.
        _embedder.Verify(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _vectorStore.Verify(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _llm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AskAsync_EmbedderDown_ReturnsUnavailable()
    {
        _embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmbeddingUnavailableException("ollama embedder down"));

        var outcome = await CreateSut().AskAsync(new AskQuery("q"), CancellationToken.None);

        outcome.Kind.Should().Be(AskOutcomeKind.Unavailable);
    }

    [Fact]
    public async Task AskAsync_QdrantDown_ReturnsUnavailable()
    {
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VectorStoreUnavailableException("qdrant down"));

        var outcome = await CreateSut().AskAsync(new AskQuery("q"), CancellationToken.None);

        outcome.Kind.Should().Be(AskOutcomeKind.Unavailable);
    }

    [Fact]
    public async Task AskAsync_LlmDown_ReturnsUnavailable()
    {
        var docA = Guid.CreateVersion7();
        var chunkA = Guid.CreateVersion7();
        SetupHits(Hit(docA, chunkA, 0, 0.9f));
        SetupDocs(Doc(docA, "a.pdf"));
        SetupChunks(Chunk(chunkA, docA, 0, "passage"));
        _llm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmUnavailableException("chat model down"));

        var outcome = await CreateSut().AskAsync(new AskQuery("q"), CancellationToken.None);

        outcome.Kind.Should().Be(AskOutcomeKind.Unavailable);
    }

    [Fact]
    public async Task AskAsync_SearchesAcrossAllDocuments_NotScopedToOne()
    {
        Guid? capturedDocScope = Guid.CreateVersion7();
        _vectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<float[], int, Guid?, CancellationToken>((_, _, docId, _) => capturedDocScope = docId)
            .ReturnsAsync((IReadOnlyList<ChunkHit>)[]);

        await CreateSut().AskAsync(new AskQuery("q"), CancellationToken.None);

        capturedDocScope.Should().BeNull();
    }
}
