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
    private readonly Mock<IEmbeddingService> _embedder = new();
    private readonly Mock<IDocumentRepository> _documents = new();

    public DocumentProcessingWorkerTests()
    {
        // Sensible defaults so a test only sets up the pass it exercises; the other passes are empty.
        _documents
            .Setup(r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());
        _documents
            .Setup(r => r.GetNextTextExtractedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());
        _documents
            .Setup(r => r.GetNextClassifiedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());
        _processor
            .Setup(p => p.RecoverStaleClaimsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _classifier
            .Setup(c => c.RecoverStaleClassifyingAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _embedder
            .Setup(e => e.RecoverStaleGeneratingEmbeddingsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
    }

    private DocumentProcessingWorker CreateSut(
        int pollSeconds = 1,
        int stuckMinutes = 15,
        int transientBackoffTicks = 6,
        IDatabaseReadinessProbe? readinessProbe = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _processor.Object);
        services.AddScoped(_ => _classifier.Object);
        services.AddScoped(_ => _embedder.Object);
        services.AddScoped(_ => _documents.Object);
        var provider = services.BuildServiceProvider();

        var options = Options.Create(new WorkerOptions
        {
            PollIntervalSeconds = pollSeconds,
            StuckResetMinutes = stuckMinutes,
            TransientBackoffTicks = transientBackoffTicks,
        });

        // DA-044-D1: default to an always-ready probe so these poll-loop tests start ticking
        // immediately (the readiness gate is covered separately in DatabaseReadinessGateTests and
        // the Gate_* test below). A test may inject a not-yet-ready probe to assert the gate blocks.
        IDatabaseReadinessProbe probe = readinessProbe ?? AlwaysReadyProbe();

        return new DocumentProcessingWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            probe,
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

    private static IDatabaseReadinessProbe AlwaysReadyProbe()
    {
        var probe = new Mock<IDatabaseReadinessProbe>();
        probe.Setup(p => p.IsReadyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return probe.Object;
    }

    // ----------------------------------------------------------------------------------------
    // Phase 9 — DA-044-D1: the boot-time DB-readiness gate. The poll loop must NOT tick until the
    // readiness probe reports ready (reachable + fully migrated) — this is what removes the
    // transient `Invalid object name 'AuditLogs'` boot noise on a fresh stack.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Gate_WaitsForReadiness_DoesNotPollUntilDbIsMigrated()
    {
        // The probe reports NOT ready for the first few calls, then ready — modelling the API still
        // applying migrations. The poll loop's first selection query (GetNextQueuedIdsAsync) must
        // NOT fire until the probe flips to ready.
        var id = Guid.CreateVersion7();
        _documents
            .SetupSequence(r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id })
            .ReturnsAsync(Array.Empty<Guid>());

        var probeCalls = 0;
        var probe = new Mock<IDatabaseReadinessProbe>();
        probe
            .Setup(p => p.IsReadyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref probeCalls) >= 3); // not ready twice, then ready

        var firstPoll = new TaskCompletionSource();
        _documents
            .Setup(r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => firstPoll.TrySetResult())
            .ReturnsAsync(Array.Empty<Guid>());

        var processed = new TaskCompletionSource();
        _processor
            .Setup(p => p.ProcessAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() => processed.TrySetResult())
            .ReturnsAsync(ProcessingOutcome.Succeeded);

        // pollSeconds=1 so the gate's 2s probe-backoff dominates the early boot window.
        var sut = CreateSut(pollSeconds: 1, readinessProbe: probe.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sut.StartAsync(cts.Token);

        // The loop must only begin once the probe reported ready (≥ 3 probe calls).
        await Task.WhenAny(firstPoll.Task, Task.Delay(TimeSpan.FromSeconds(12), cts.Token));
        await sut.StopAsync(CancellationToken.None);

        firstPoll.Task.IsCompletedSuccessfully.Should().BeTrue("the poll loop should start after the DB became ready");
        probeCalls.Should().BeGreaterThanOrEqualTo(3, "the gate must keep probing while the DB is not yet migrated");
    }

    [Fact]
    public async Task Gate_NeverReady_DoesNotPoll_AndStopsCleanly()
    {
        // A perpetually-not-ready DB: the gate must keep waiting (no crash) and the poll loop must
        // never run a selection query. Stopping the host must still be clean.
        var probe = new Mock<IDatabaseReadinessProbe>();
        probe.Setup(p => p.IsReadyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = CreateSut(pollSeconds: 1, readinessProbe: probe.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token); // a few probe cycles
        await sut.StopAsync(CancellationToken.None);

        _documents.Verify(
            r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the poll loop must never tick while the DB is not ready");
        probe.Verify(p => p.IsReadyAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
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

    // ----------------------------------------------------------------------------------------
    // Phase 5 — DA-040: embedding pass (pass 3), Transient backoff, GeneratingEmbeddings stale sweep.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Pass3_DrainsClassifiedIdsFifo_AndCallsEmbedDocumentAsyncPerId()
    {
        var id1 = Guid.CreateVersion7();
        var id2 = Guid.CreateVersion7();

        _documents
            .SetupSequence(r => r.GetNextClassifiedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id1, id2 })   // first tick: two docs
            .ReturnsAsync(Array.Empty<Guid>()); // subsequent ticks: empty

        var embedded = new List<Guid>();
        var bothDone = new TaskCompletionSource();
        _embedder
            .Setup(e => e.EmbedDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((id, _) =>
            {
                lock (embedded)
                {
                    embedded.Add(id);
                    if (embedded.Count == 2)
                    {
                        bothDone.TrySetResult();
                    }
                }
            })
            .ReturnsAsync(ProcessingOutcome.Succeeded);

        await RunUntilAsync(CreateSut(), bothDone.Task);

        embedded.Should().Equal(id1, id2); // FIFO order preserved
    }

    [Fact]
    public async Task Pass3_NotClaimed_SkipsAndContinues()
    {
        var id = Guid.CreateVersion7();
        _documents
            .SetupSequence(r => r.GetNextClassifiedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id })
            .ReturnsAsync(Array.Empty<Guid>());

        var called = new TaskCompletionSource();
        _embedder
            .Setup(e => e.EmbedDocumentAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() => called.TrySetResult())
            .ReturnsAsync(ProcessingOutcome.NotClaimed); // lost the race — must not throw, just move on

        await RunUntilAsync(CreateSut(), called.Task);

        _embedder.Verify(e => e.EmbedDocumentAsync(id, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Pass3_OneDocumentThrows_DoesNotCrashLoop_AndEmbedsNext()
    {
        var poison = Guid.CreateVersion7();
        var good = Guid.CreateVersion7();

        _documents
            .SetupSequence(r => r.GetNextClassifiedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { poison, good })
            .ReturnsAsync(Array.Empty<Guid>());

        var goodDone = new TaskCompletionSource();
        _embedder
            .Setup(e => e.EmbedDocumentAsync(poison, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _embedder
            .Setup(e => e.EmbedDocumentAsync(good, It.IsAny<CancellationToken>()))
            .Callback(() => goodDone.TrySetResult())
            .ReturnsAsync(ProcessingOutcome.Succeeded);

        await RunUntilAsync(CreateSut(), goodDone.Task);

        _embedder.Verify(e => e.EmbedDocumentAsync(good, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Pass3_Transient_LeavesDocAndBacksOff_DoesNotHotLoopTheEmbedder()
    {
        // A perpetually-down embedder/Qdrant: every embed returns Transient. With a backoff of N
        // ticks, the embedding pass must NOT call EmbedDocumentAsync every tick — after the first
        // Transient it skips the pass for N ticks. We let it run for a while and assert the embedder
        // was hit only a SMALL number of times, not once per tick.
        var id = Guid.CreateVersion7();
        _documents
            .Setup(r => r.GetNextClassifiedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { id }); // doc stays Classified (Transient never advances it)

        var firstHit = new TaskCompletionSource();
        var calls = 0;
        _embedder
            .Setup(e => e.EmbedDocumentAsync(id, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Interlocked.Increment(ref calls);
                firstHit.TrySetResult();
            })
            .ReturnsAsync(ProcessingOutcome.Transient);

        // pollSeconds=1, backoff=5: without backoff an un-throttled loop would call many times in a
        // few seconds; with backoff it should call roughly once per (1 + 5) ticks.
        var sut = CreateSut(pollSeconds: 1, transientBackoffTicks: 5);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.StartAsync(cts.Token);
        await Task.WhenAny(firstHit.Task, Task.Delay(TimeSpan.FromSeconds(8), cts.Token));
        await Task.Delay(TimeSpan.FromSeconds(4), cts.Token); // ~4 ticks of backoff window
        await sut.StopAsync(CancellationToken.None);

        calls.Should().BeGreaterThan(0, "the embedding pass must run at least once");
        // Without backoff this would be ~4-5 calls in 4s at 1s/tick; with a 5-tick backoff it is far fewer.
        calls.Should().BeLessThan(4, "the Transient backoff must prevent hot-looping a down embedder/Qdrant");
    }

    [Fact]
    public async Task Tick_RunsGeneratingEmbeddingsStaleSweepEachTick_WithConfiguredThreshold()
    {
        var swept = new TaskCompletionSource();
        _embedder
            .Setup(e => e.RecoverStaleGeneratingEmbeddingsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((threshold, _) =>
            {
                threshold.Should().Be(TimeSpan.FromMinutes(15)); // from StuckResetMinutes
                swept.TrySetResult();
            })
            .ReturnsAsync(0);

        await RunUntilAsync(CreateSut(stuckMinutes: 15), swept.Task);

        _embedder.Verify(e => e.RecoverStaleGeneratingEmbeddingsAsync(
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Passes_RunInOrder_ExtractionThenClassificationThenEmbedding_EachTick()
    {
        // Fairness/ordering: pass 1 (extraction) -> pass 2 (classification) -> pass 3 (embedding)
        // within a single tick. Record the relative order of the first call into each pass.
        var qId = Guid.CreateVersion7();
        var tId = Guid.CreateVersion7();
        var cId = Guid.CreateVersion7();

        _documents.Setup(r => r.GetNextQueuedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { qId });
        _documents.Setup(r => r.GetNextTextExtractedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { tId });
        _documents.Setup(r => r.GetNextClassifiedIdsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { cId });

        var order = new List<string>();
        var allDone = new TaskCompletionSource();
        _processor.Setup(p => p.ProcessAsync(qId, It.IsAny<CancellationToken>()))
            .Callback(() => { lock (order) order.Add("extract"); })
            .ReturnsAsync(ProcessingOutcome.Succeeded);
        _classifier.Setup(c => c.ClassifyAsync(tId, It.IsAny<CancellationToken>()))
            .Callback(() => { lock (order) order.Add("classify"); })
            .ReturnsAsync(ProcessingOutcome.Succeeded);
        _embedder.Setup(e => e.EmbedDocumentAsync(cId, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                lock (order)
                {
                    order.Add("embed");
                    allDone.TrySetResult();
                }
            })
            .ReturnsAsync(ProcessingOutcome.Succeeded);

        await RunUntilAsync(CreateSut(), allDone.Task);

        // The first three recorded calls must be in pipeline order.
        order.Take(3).Should().Equal("extract", "classify", "embed");
    }
}
