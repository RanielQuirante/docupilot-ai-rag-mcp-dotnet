using DocuPilot.Worker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DocuPilot.UnitTests.Worker;

/// <summary>
/// Unit tests for <see cref="DatabaseReadinessGate"/> (DA-044-D1) — the bounded-backoff wait loop the
/// Worker uses to gate its first poll tick on a migrated DB. Tested in isolation against a fake
/// <see cref="IDatabaseReadinessProbe"/> with a tiny poll delay so the tests are fast: the gate must
/// proceed as soon as the probe reports ready, keep retrying while it is not (never crashing), and
/// observe cancellation cleanly when the host shuts down before readiness.
/// </summary>
public sealed class DatabaseReadinessGateTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(10);

    private static DatabaseReadinessGate Gate(IDatabaseReadinessProbe probe) =>
        new(probe, NullLogger.Instance, FastPoll);

    [Fact]
    public async Task WaitUntilReady_ProbeReadyImmediately_ReturnsTrue_ProbesOnce()
    {
        var probe = new Mock<IDatabaseReadinessProbe>();
        probe.Setup(p => p.IsReadyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Gate(probe.Object).WaitUntilReadyAsync(CancellationToken.None);

        result.Should().BeTrue();
        probe.Verify(p => p.IsReadyAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitUntilReady_PendingMigrations_WaitsThenProceedsWhenReady()
    {
        // Models the API still migrating: the probe is NOT ready for the first two calls (pending
        // migrations / DB warming up), then ready. The gate must keep polling and only return true
        // once readiness is reported.
        var calls = 0;
        var probe = new Mock<IDatabaseReadinessProbe>();
        probe
            .Setup(p => p.IsReadyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref calls) >= 3);

        var result = await Gate(probe.Object).WaitUntilReadyAsync(CancellationToken.None);

        result.Should().BeTrue();
        calls.Should().BeGreaterThanOrEqualTo(3, "the gate must wait while migrations are still pending");
    }

    [Fact]
    public async Task WaitUntilReady_NeverReady_KeepsRetrying_DoesNotCrash_UntilCancelled()
    {
        // A never-ready DB must NOT crash the gate — it keeps probing until cancellation, then
        // returns false (host shutting down before readiness). Robustness requirement of DA-044-D1.
        var calls = 0;
        var probe = new Mock<IDatabaseReadinessProbe>();
        probe
            .Setup(p => p.IsReadyAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref calls))
            .ReturnsAsync(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await Gate(probe.Object).WaitUntilReadyAsync(cts.Token);

        result.Should().BeFalse("cancellation before readiness returns false rather than throwing");
        calls.Should().BeGreaterThan(1, "the gate must keep retrying a never-ready DB rather than give up after one probe");
    }

    [Fact]
    public async Task WaitUntilReady_AlreadyCancelled_ReturnsFalse_WithoutThrowing()
    {
        var probe = new Mock<IDatabaseReadinessProbe>();
        probe.Setup(p => p.IsReadyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Gate(probe.Object).WaitUntilReadyAsync(cts.Token);

        result.Should().BeFalse();
    }
}
