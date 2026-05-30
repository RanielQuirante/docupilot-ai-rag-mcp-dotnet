using DocuPilot.Models.Entities;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Documents;
using DocuPilot.Worker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DocuPilot.UnitTests.Worker;

/// <summary>
/// Unit tests for <see cref="DocumentProcessingWorker"/> — the Phase-3 poller. The worker is a
/// singleton that resolves scoped collaborators per document via <see cref="IServiceScopeFactory"/>,
/// so these tests build a real <see cref="ServiceProvider"/> with the orchestrator and the
/// document repository registered as SCOPED mocks. We drive one (or a few) real ticks by starting
/// the <see cref="BackgroundService"/>, waiting on a signal from the mock, then stopping it —
/// asserting the claim-loop drains FIFO ids, skips on <see cref="ProcessingOutcome.NotClaimed"/>,
/// runs the stale-claim sweep each tick, and isolates a throwing document.
/// </summary>
public sealed class DocumentProcessingWorkerTests
{
    private readonly Mock<IDocumentProcessingService> _processor = new();
    private readonly Mock<IDocumentRepository> _documents = new();

    private DocumentProcessingWorker CreateSut(int pollSeconds = 1, int stuckMinutes = 15)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _processor.Object);
        services.AddScoped(_ => _documents.Object);
        var provider = services.BuildServiceProvider();

        var options = Options.Create(new WorkerOptions
        {
            PollIntervalSeconds = pollSeconds,
            StuckResetMinutes = stuckMinutes,
        });

        return new DocumentProcessingWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<DocumentProcessingWorker>.Instance);
    }

    /// <summary>Runs the service until <paramref name="signal"/> completes, then stops it cleanly.</summary>
    private static async Task RunUntilAsync(DocumentProcessingWorker sut, Task signal)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.StartAsync(cts.Token);
        var completed = await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(8), cts.Token));
        await sut.StopAsync(CancellationToken.None);
        completed.Should().Be(signal, "the worker should have done its work before the timeout");
    }

    [Fact]
    public async Task Tick_DrainsQueuedIdsFifo_AndCallsProcessAsyncPerId()
    {
        var id1 = Guid.CreateVersion7();
        var id2 = Guid.CreateVersion7();

        _documents
            .SetupSequence(r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id1, id2 })   // first tick: two docs
            .ReturnsAsync(Array.Empty<Guid>()); // subsequent ticks: empty

        var processed = new List<Guid>();
        var bothDone = new TaskCompletionSource();
        _processor
            .Setup(p => p.ProcessAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((id, _) =>
            {
                lock (processed)
                {
                    processed.Add(id);
                    if (processed.Count == 2)
                    {
                        bothDone.TrySetResult();
                    }
                }
            })
            .ReturnsAsync(ProcessingOutcome.Succeeded);

        await RunUntilAsync(CreateSut(), bothDone.Task);

        processed.Should().Equal(id1, id2); // FIFO order preserved
    }

    [Fact]
    public async Task Tick_NotClaimed_SkipsAndContinues()
    {
        var id = Guid.CreateVersion7();
        _documents
            .SetupSequence(r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id })
            .ReturnsAsync(Array.Empty<Guid>());

        var called = new TaskCompletionSource();
        _processor
            .Setup(p => p.ProcessAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() => called.TrySetResult())
            .ReturnsAsync(ProcessingOutcome.NotClaimed); // lost the race — must not throw, just move on

        await RunUntilAsync(CreateSut(), called.Task);

        // Reaching here (no exception, clean stop) proves NotClaimed is handled benignly.
        _processor.Verify(p => p.ProcessAsync(id, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Tick_OneDocumentThrows_DoesNotCrashLoop_AndProcessesNext()
    {
        var poison = Guid.CreateVersion7();
        var good = Guid.CreateVersion7();

        _documents
            .SetupSequence(r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { poison, good })
            .ReturnsAsync(Array.Empty<Guid>());

        var goodDone = new TaskCompletionSource();
        _processor
            .Setup(p => p.ProcessAsync(poison, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _processor
            .Setup(p => p.ProcessAsync(good, It.IsAny<CancellationToken>()))
            .Callback(() => goodDone.TrySetResult())
            .ReturnsAsync(ProcessingOutcome.Succeeded);

        await RunUntilAsync(CreateSut(), goodDone.Task);

        // The poison document's exception was swallowed; the next document still processed.
        _processor.Verify(p => p.ProcessAsync(good, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Tick_RunsStaleClaimSweepEachTick()
    {
        _documents
            .Setup(r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());

        var swept = new TaskCompletionSource();
        _processor
            .Setup(p => p.RecoverStaleClaimsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((threshold, _) =>
            {
                threshold.Should().Be(TimeSpan.FromMinutes(15)); // from StuckResetMinutes
                swept.TrySetResult();
            })
            .ReturnsAsync(0);

        await RunUntilAsync(CreateSut(stuckMinutes: 15), swept.Task);

        _processor.Verify(p => p.RecoverStaleClaimsAsync(
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
