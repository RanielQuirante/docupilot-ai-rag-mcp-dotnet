using DocuPilot.Services.Abstractions;

namespace DocuPilot.IntegrationTests.Stubs;

/// <summary>
/// Deterministic in-process stubs for the three external-service ports (<see cref="ILlmClient"/>,
/// <see cref="IEmbeddingClient"/>, <see cref="IVectorStore"/>) so the integration tests exercise the
/// real API ↔ EF ↔ repository wiring with ZERO network (no Ollama, no Qdrant). The factory registers
/// these in place of the Ollama/Qdrant implementations (DA-062). Each stub is mutable so a single test
/// can script the exact response it needs (e.g. the vector store returns a known hit, or no hits).
/// </summary>
public sealed class StubEmbeddingClient : IEmbeddingClient
{
    /// <summary>The fixed dimensionality the stub reports (matches the nomic-embed-text default, 768).</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>When set, <see cref="EmbedAsync"/> throws this to drive the 503 unavailable path.</summary>
    public Exception? ThrowOnEmbed { get; set; }

    public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct)
    {
        if (ThrowOnEmbed is not null)
        {
            throw ThrowOnEmbed;
        }

        // A deterministic, non-zero vector of the configured length. The exact values are irrelevant
        // because the StubVectorStore ignores the query vector and returns scripted hits.
        var vector = new float[Dimensions];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = 0.01f;
        }

        return Task.FromResult(new EmbeddingResult(vector, "stub-embed", TimeSpan.Zero));
    }
}

/// <summary>Deterministic <see cref="IVectorStore"/> stub. Returns whatever hits the test scripts.</summary>
public sealed class StubVectorStore : IVectorStore
{
    /// <summary>Scripted hits returned by <see cref="SearchAsync"/> (empty by default ⇒ "found nothing").</summary>
    public List<ChunkHit> Hits { get; } = [];

    /// <summary>When set, <see cref="SearchAsync"/> throws this to drive the 503 unavailable path.</summary>
    public Exception? ThrowOnSearch { get; set; }

    public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

    public Task UpsertChunksAsync(IReadOnlyList<ChunkVector> chunks, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<ChunkHit>> SearchAsync(float[] query, int limit, Guid? documentId, CancellationToken ct)
    {
        if (ThrowOnSearch is not null)
        {
            throw ThrowOnSearch;
        }

        IReadOnlyList<ChunkHit> result = Hits.Take(limit).ToList();
        return Task.FromResult(result);
    }
}

/// <summary>
/// Deterministic <see cref="ILlmClient"/> stub. Returns a fixed canned completion. The recommend-workflow
/// tool expects a JSON object; the default content is a valid recommendation JSON so the recommend path
/// returns 200. A test may override <see cref="Content"/> for other shapes.
/// </summary>
public sealed class StubLlmClient : ILlmClient
{
    /// <summary>The raw completion text returned to the orchestrator. Defaults to a valid recommendation JSON.</summary>
    public string Content { get; set; } =
        """{ "recommendedWorkflow": "Manual Review", "nextStep": "Assign to the operations team", "priority": "Normal", "reason": "Stubbed deterministic recommendation for integration tests." }""";

    /// <summary>When set, <see cref="CompleteAsync"/> throws this to drive the 503 unavailable path.</summary>
    public Exception? ThrowOnComplete { get; set; }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (ThrowOnComplete is not null)
        {
            throw ThrowOnComplete;
        }

        return Task.FromResult(new LlmResponse(Content, "stub-llm", TimeSpan.Zero));
    }
}
