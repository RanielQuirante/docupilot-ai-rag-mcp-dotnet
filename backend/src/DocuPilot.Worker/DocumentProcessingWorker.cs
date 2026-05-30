using DocuPilot.Services.Documents;
using Microsoft.Extensions.Options;

namespace DocuPilot.Worker;

/// <summary>
/// The Phase-3 document-processing poller (ADR §1/§6). A hosted <see cref="BackgroundService"/>
/// that, on a fixed interval (<c>Worker:PollIntervalSeconds</c>):
/// <list type="number">
///   <item>runs the stale-claim sweep (resets <c>ExtractingText</c> docs stuck past
///   <c>Worker:StuckResetMinutes</c> back to <c>Queued</c> — crash/restart self-heal);</item>
///   <item>selects the oldest <c>Queued</c> documents (FIFO by <c>UploadedAt ASC</c>) and, for
///   each, creates a DI <b>scope per document</b> and delegates to
///   <see cref="IDocumentProcessingService.ProcessAsync"/> — which claims atomically and returns
///   <see cref="ProcessingOutcome.NotClaimed"/> if it loses the race (we just skip it).</item>
/// </list>
/// The service is a singleton; <see cref="IDocumentProcessingService"/>, the repositories and the
/// <c>DocuPilotDbContext</c> are scoped, so each document is processed inside its own
/// <see cref="IServiceScope"/> (no captive-dependency / singleton-captures-scoped error). Every
/// dependency it resolves comes from the SHARED <c>AddServices()</c> / <c>AddInfrastructure()</c>
/// extensions (lessons.md DA-021), so the Worker and API compose identical graphs.
/// </summary>
/// <remarks>
/// Resilience (ADR §6): each per-document call and each tick are wrapped in try/catch — one
/// poisoned/throwing document never crashes the host loop. The poller honors
/// <paramref name="stoppingToken"/> for instant, graceful shutdown; host cancellation during
/// extraction is re-thrown by the orchestrator (the doc stays <c>ExtractingText</c> for the
/// stale-claim sweep) — never recorded as a <c>Failed</c> extraction.
/// </remarks>
public sealed class DocumentProcessingWorker : BackgroundService
{
    /// <summary>How many queued documents to drain per tick (cheap indexed selection).</summary>
    private const int MaxBatchPerTick = 25;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkerOptions _options;
    private readonly ILogger<DocumentProcessingWorker> _logger;

    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleThreshold;

    public DocumentProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<WorkerOptions> options,
        ILogger<DocumentProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;

        // Defensive floors so a mis-set config can't spin the loop or disable the sweep.
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        _staleThreshold = TimeSpan.FromMinutes(Math.Max(1, _options.StuckResetMinutes));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Document-processing worker started. Poll interval: {PollSeconds}s; stale-claim threshold: {StaleMinutes}m.",
            _pollInterval.TotalSeconds, _staleThreshold.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStaleClaimsAsync(stoppingToken);
                await DrainQueuedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown — not an error
            }
            catch (Exception ex)
            {
                // A failure in the tick itself (e.g. DB unreachable) must not kill the host —
                // log and try again next interval.
                _logger.LogError(ex, "Unhandled error in the processing tick; continuing after the next interval.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown during the inter-poll delay
            }
        }

        _logger.LogInformation("Document-processing worker stopping.");
    }

    /// <summary>Runs the stale-claim sweep in its own scope; isolates failures from the drain.</summary>
    private async Task RecoverStaleClaimsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IDocumentProcessingService>();

        var reset = await processor.RecoverStaleClaimsAsync(_staleThreshold, stoppingToken);
        if (reset > 0)
        {
            _logger.LogInformation("Stale-claim sweep reset {Count} document(s) back to Queued.", reset);
        }
    }

    /// <summary>
    /// Selects the oldest Queued documents (FIFO) and processes each in its own DI scope. Per-doc
    /// try/catch so one poison document never aborts the batch or the loop.
    /// </summary>
    private async Task DrainQueuedAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<Guid> ids;
        using (var selectionScope = _scopeFactory.CreateScope())
        {
            var documents = selectionScope.ServiceProvider
                .GetRequiredService<DocuPilot.Repository.Abstractions.IDocumentRepository>();
            ids = await documents.GetNextQueuedIdsAsync(MaxBatchPerTick, stoppingToken);
        }

        foreach (var id in ids)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await ProcessOneAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // bubble up to the loop for a clean shutdown
            }
            catch (Exception ex)
            {
                // Isolation: an unexpected fault on one document logs and the loop continues.
                // The orchestrator already converts extraction faults into a Failed terminal,
                // so reaching here means an infrastructure/transaction-level surprise.
                _logger.LogError(ex, "Unexpected error processing document {DocumentId}; skipping it.", id);
            }
        }
    }

    /// <summary>Processes a single document inside its own scope (scoped DbContext/repos/orchestrator).</summary>
    private async Task ProcessOneAsync(Guid id, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IDocumentProcessingService>();

        var outcome = await processor.ProcessAsync(id, stoppingToken);
        switch (outcome)
        {
            case ProcessingOutcome.Succeeded:
                _logger.LogInformation("Document {DocumentId} processed: text extracted.", id);
                break;
            case ProcessingOutcome.Failed:
                _logger.LogWarning("Document {DocumentId} processing failed (marked Failed with a reason).", id);
                break;
            case ProcessingOutcome.NotClaimed:
                // Another iteration/worker won the claim, or the row left Queued between selection
                // and claim. Expected and benign — just move on.
                _logger.LogDebug("Document {DocumentId} was already claimed; skipping.", id);
                break;
            case ProcessingOutcome.NotFound:
                _logger.LogDebug("Document {DocumentId} no longer exists; skipping.", id);
                break;
        }
    }
}
