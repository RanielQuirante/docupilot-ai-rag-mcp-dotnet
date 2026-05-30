using DocuPilot.Infrastructure.Vector;
using DocuPilot.Services.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DocuPilot.UnitTests.Vector;

/// <summary>
/// Unit tests for <see cref="QdrantCollectionBootstrapper"/> — the shared startup task that ensures
/// the Qdrant collection (ADR §3/§8). The <see cref="IVectorStore"/> + <see cref="IEmbeddingClient"/>
/// are STUBBED (no network). Covers: dimension mismatch fails LOUD (throws, does not swallow);
/// Qdrant-not-ready is tolerated (logs + returns without crashing); happy path ensures once at the
/// embedder's dimension.
/// </summary>
public sealed class QdrantCollectionBootstrapperTests
{
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IEmbeddingClient> _embedder = new();

    public QdrantCollectionBootstrapperTests()
    {
        _embedder.SetupGet(e => e.Dimensions).Returns(768);
    }

    private QdrantCollectionBootstrapper CreateSut(int maxAttempts = 3)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _vectorStore.Object);
        services.AddScoped(_ => _embedder.Object);
        var provider = services.BuildServiceProvider();

        return new QdrantCollectionBootstrapper(
            provider,
            NullLogger<QdrantCollectionBootstrapper>.Instance,
            maxAttempts: maxAttempts,
            delay: TimeSpan.Zero);
    }

    [Fact]
    public async Task StartAsync_HappyPath_EnsuresCollectionAtEmbedderDimension()
    {
        _vectorStore.Setup(v => v.EnsureCollectionAsync(768, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateSut().StartAsync(CancellationToken.None);

        _vectorStore.Verify(v => v.EnsureCollectionAsync(768, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_DimensionMismatch_FailsLoud()
    {
        _vectorStore.Setup(v => v.EnsureCollectionAsync(768, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vector size 1024 != 768"));

        var act = () => CreateSut().StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1024*");
    }

    [Fact]
    public async Task StartAsync_QdrantNotReady_ToleratedDoesNotThrowAfterRetries()
    {
        _vectorStore.Setup(v => v.EnsureCollectionAsync(768, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VectorStoreUnavailableException("qdrant unreachable"));

        var act = () => CreateSut(maxAttempts: 3).StartAsync(CancellationToken.None);

        // Tolerant of Qdrant-not-ready: logs + returns WITHOUT crashing the host (ADR §3/§8).
        await act.Should().NotThrowAsync();
        _vectorStore.Verify(v => v.EnsureCollectionAsync(768, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
