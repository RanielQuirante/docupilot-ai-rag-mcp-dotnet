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
    private readonly Mock<IClassificationService> _classifier = new();
    private readonly Mock<IDocumentRepository> _documents = new();

    public DocumentProcessingWorkerTests()
    {
        // Sensible defaults so a test only sets up the pass it exercises; the other pass is empty.
        _documents
            .Setup(r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());
        _documents
            .Setup(r => r.GetNextTextExtractedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());
        _processor
            .Setup(p => p.RecoverStaleClaimsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _classifier
            .Setup(c => c.RecoverStaleClassifyingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
    }

    private DocumentProcessingWorker CreateSut(int pollSeconds = 1, int stuckMinutes = 15, int transientBackoffTicks = 6)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _processor.Object);
        services.AddScoped(_ => _classifier.Object);
        services.AddScoped(_ => _documents.Object);
        var provider = services.BuildServiceProvider();

        var options = Options.Create(new WorkerOptions
        {
            PollIntervalSeconds = pollSeconds,
            StuckResetMinutes = stuckMinutes,
            TransientBackoffTicks = transientBackoffTicks,
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

    // ----------------------------------------------------------------------------------------
    // Phase 4 — DA-033: classification pass (pass 2), Transient backoff, Classifying stale sweep.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Pass2_DrainsTextExtractedIdsFifo_AndCallsClassifyAsyncPerId()
    {
        var id1 = Guid.CreateVersion7();
        var id2 = Guid.CreateVersion7();

        _documents
            .SetupSequence(r => r.GetNextTextExtractedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id1, id2 })   // first tick: two docs
            .ReturnsAsync(Array.Empty<Guid>()); // subsequent ticks: empty

        var classified = new List<Guid>();
        var bothDone = new TaskCompletionSource();
        _classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((id, _) =>
            {
                lock (classified)
                {
                    classified.Add(id);
                    if (classified.Count == 2)
                    {
                        bothDone.TrySetResult();
                    }
                }
            })
            .ReturnsAsync(ProcessingOutcome.Succeeded);

        await RunUntilAsync(CreateSut(), bothDone.Task);

        classified.Should().Equal(id1, id2); // FIFO order preserved
    }

    [Fact]
    public async Task Pass2_NotClaimed_SkipsAndContinues()
    {
        var id = Guid.CreateVersion7();
        _documents
            .SetupSequence(r => r.GetNextTextExtractedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id })
            .ReturnsAsync(Array.Empty<Guid>());

        var called = new TaskCompletionSource();
        _classifier
            .Setup(c => c.ClassifyAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() => called.TrySetResult())
            .ReturnsAsync(ProcessingOutcome.NotClaimed); // lost the race — must not throw, just move on

        await RunUntilAsync(CreateSut(), called.Task);

        _classifier.Verify(c => c.ClassifyAsync(id, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Pass2_OneDocumentThrows_DoesNotCrashLoop_AndClassifiesNext()
    {
        var poison = Guid.CreateVersion7();
        var good = Guid.CreateVersion7();

        _documents
            .SetupSequence(r => r.GetNextTextExtractedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { poison, good })
            .ReturnsAsync(Array.Empty<Guid>());

        var goodDone = new TaskCompletionSource();
        _classifier
            .Setup(c => c.ClassifyAsync(poison, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _classifier
            .Setup(c => c.ClassifyAsync(good, It.IsAny<CancellationToken>()))
            .Callback(() => goodDone.TrySetResult())
            .ReturnsAsync(ProcessingOutcome.Succeeded);

        await RunUntilAsync(CreateSut(), goodDone.Task);

        _classifier.Verify(c => c.ClassifyAsync(good, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Pass2_Transient_LeavesDocAndBacksOff_DoesNotHotLoopTheLlm()
    {
        // A perpetually-down LLM: every classify returns Transient. With a backoff of N ticks, the
        // classification pass must NOT call ClassifyAsync every tick — after the first Transient it
        // skips the pass for N ticks. We let it run for a while (many poll intervals) and assert the
        // LLM was hit only a SMALL number of times, not once per tick.
        var id = Guid.CreateVersion7();
        _documents
            .Setup(r => r.GetNextTextExtractedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id }); // doc stays TextExtracted (Transient never advances it)

        var firstHit = new TaskCompletionSource();
        var calls = 0;
        _classifier
            .Setup(c => c.ClassifyAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Interlocked.Increment(ref calls);
                firstHit.TrySetResult();
            })
            .ReturnsAsync(ProcessingOutcome.Transient);

        // pollSeconds=1, backoff=5: in a few seconds an un-backed-off loop would call many times;
        // with backoff it should call roughly once per (1 + 5) ticks.
        var sut = CreateSut(pollSeconds: 1, transientBackoffTicks: 5);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.StartAsync(cts.Token);
        await Task.WhenAny(firstHit.Task, Task.Delay(TimeSpan.FromSeconds(8), cts.Token));
        await Task.Delay(TimeSpan.FromSeconds(4), cts.Token); // ~4 ticks of backoff window
        await sut.StopAsync(CancellationToken.None);

        calls.Should().BeGreaterThan(0, "the classify pass must run at least once");
        // Without backoff this would be ~4-5 calls in 4s at 1s/tick; with a 5-tick backoff it is far fewer.
        calls.Should().BeLessThan(4, "the Transient backoff must prevent hot-looping a down LLM");
    }

    [Fact]
    public async Task Tick_RunsClassifyingStaleSweepEachTick_WithConfiguredThreshold()
    {
        var swept = new TaskCompletionSource();
        _classifier
            .Setup(c => c.RecoverStaleClassifyingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((threshold, _) =>
            {
                threshold.Should().Be(TimeSpan.FromMinutes(15)); // from StuckResetMinutes
                swept.TrySetResult();
            })
            .ReturnsAsync(0);

        await RunUntilAsync(CreateSut(stuckMinutes: 15), swept.Task);

        _classifier.Verify(c => c.RecoverStaleClassifyingAsync(
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
