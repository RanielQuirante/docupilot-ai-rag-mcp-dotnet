using DocuPilot.Services.Documents;
using Microsoft.Extensions.Options;

namespace DocuPilot.Worker;

/// <summary>
/// The two-pass document-processing poller (ADR §1/§6, Phase-3 DA-025 + Phase-4 DA-033). A hosted
/// <see cref="BackgroundService"/> that, on a fixed interval (<c>Worker:PollIntervalSeconds</c>),
/// drives BOTH pipeline stages each tick:
/// <list type="number">
///   <item><b>Stale-claim sweep</b> — resets <c>ExtractingText</c> docs stuck past
///   <c>Worker:StuckResetMinutes</c> back to <c>Queued</c> AND <c>Classifying</c> docs stuck past
///   the same threshold back to <c>TextExtracted</c> (crash/restart self-heal, audit-timestamp
///   approach — no <c>ClaimedAt</c> column).</item>
///   <item><b>Pass 1 — extraction</b>: selects the oldest <c>Queued</c> documents (FIFO by
///   <c>UploadedAt ASC</c>) and, per document in its own DI scope, delegates to
///   <see cref="IDocumentProcessingService.ProcessAsync"/> (claims atomically internally).</item>
///   <item><b>Pass 2 — classification</b>: selects the oldest <c>TextExtracted</c> documents (FIFO)
///   and, per document in its own DI scope, delegates to
///   <see cref="IClassificationService.ClassifyAsync"/> (claims atomically internally; calls the
///   LLM). A <see cref="ProcessingOutcome.Transient"/> (LLM down / model missing) leaves the doc
///   <c>TextExtracted</c> and triggers a short backoff so a down LLM is not spammed every tick.</item>
///   <item><b>Pass 3 — embedding</b> (DA-040): selects the oldest <c>Classified</c> documents (FIFO)
///   and, per document in its own DI scope, delegates to
///   <see cref="IEmbeddingService.EmbedDocumentAsync"/> (claims atomically internally; chunks,
///   embeds, writes Qdrant + SQL). A <see cref="ProcessingOutcome.Transient"/> (embedder/Qdrant
///   down) leaves the doc <c>Classified</c> and triggers the SAME backoff so a down embedder/Qdrant
///   is not spammed every tick.</item>
/// </list>
/// <para>
/// <b>Fairness/ordering:</b> extraction (pass 1) drains BEFORE classification (pass 2) each tick.
/// Extraction is fast (local I/O) so newly-uploaded docs reach <c>TextExtracted</c> promptly;
/// classification is slow (CPU LLM, seconds–minutes per doc) so it proceeds at its own pace after.
/// Pass 3 (embedding) runs last for the same reason — classification feeds its <c>Classified</c>
/// backlog. None starve because all three passes run every tick, each capped at
/// <see cref="MaxBatchPerTick"/>; an earlier pass feeds the next pass's backlog, never blocks it
/// (separate selection queries + separate scopes).
/// </para>
/// <para>
/// The service is a singleton; <see cref="IDocumentProcessingService"/>,
/// <see cref="IClassificationService"/>, the repositories and the <c>DocuPilotDbContext</c> are
/// scoped, so each document is processed inside its own <see cref="IServiceScope"/> (no
/// captive-dependency / singleton-captures-scoped error). Every dependency it resolves comes from
/// the SHARED <c>AddServices()</c> / <c>AddInfrastructure()</c> extensions (lessons.md DA-021), so
/// the Worker and API compose identical graphs.
/// </para>
/// </summary>
/// <remarks>
/// Resilience (ADR §6): each per-document call and each tick are wrapped in try/catch — one
/// poisoned/throwing document never crashes the host loop. The poller honors
/// <paramref name="stoppingToken"/> for instant, graceful shutdown; host cancellation during
/// extraction/classification is re-thrown by the orchestrators (the doc stays in its in-flight
/// state for the stale-claim sweep) — never recorded as a <c>Failed</c>.
/// </remarks>
public sealed class DocumentProcessingWorker : BackgroundService
{
    /// <summary>How many documents to drain per pass per tick (cheap indexed selection).</summary>
    private const int MaxBatchPerTick = 25;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkerOptions _options;
    private readonly ILogger<DocumentProcessingWorker> _logger;

    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleThreshold;
    private readonly int _transientBackoffTicks;

    /// <summary>
    /// Number of upcoming ticks to skip the classification pass after a Transient outcome (LLM
    /// down). Decremented each tick; pass 2 runs only when this reaches 0. Single-threaded loop, so
    /// no synchronization needed.
    /// </summary>
    private int _classifyBackoffRemaining;

    /// <summary>
    /// Number of upcoming ticks to skip the embedding pass after a Transient outcome (embedder or
    /// Qdrant down). Decremented each tick; pass 3 runs only when this reaches 0. Single-threaded
    /// loop, so no synchronization needed. Mirrors <see cref="_classifyBackoffRemaining"/>.
    /// </summary>
    private int _embedBackoffRemaining;

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
        _transientBackoffTicks = Math.Max(0, _options.TransientBackoffTicks);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Document-processing worker started. Poll interval: {PollSeconds}s; stale-claim threshold: {StaleMinutes}m; transient backoff: {BackoffTicks} ticks.",
            _pollInterval.TotalSeconds, _staleThreshold.TotalMinutes, _transientBackoffTicks);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStaleClaimsAsync(stoppingToken);
                await DrainQueuedAsync(stoppingToken);
                await DrainTextExtractedAsync(stoppingToken);
                await DrainClassifiedAsync(stoppingToken);
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

    /// <summary>
    /// Runs the generalized stale-claim sweep in its own scope; isolates failures from the drains.
    /// Resets stuck <c>ExtractingText</c> (→ <c>Queued</c>), stuck <c>Classifying</c>
    /// (→ <c>TextExtracted</c>) and stuck <c>GeneratingEmbeddings</c> (→ <c>Classified</c>)
    /// documents past the configured threshold.
    /// </summary>
    private async Task RecoverStaleClaimsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IDocumentProcessingService>();
        var classifier = scope.ServiceProvider.GetRequiredService<IClassificationService>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        var resetExtracting = await processor.RecoverStaleClaimsAsync(_staleThreshold, stoppingToken);
        if (resetExtracting > 0)
        {
            _logger.LogInformation("Stale-claim sweep reset {Count} document(s) back to Queued.", resetExtracting);
        }

        // Phase-4 (DA-033): generalize the sweep to the classification stage — a doc stuck in
        // Classifying (worker crashed mid-LLM) self-heals back to TextExtracted for re-claim.
        var resetClassifying = await classifier.RecoverStaleClassifyingAsync(_staleThreshold, stoppingToken);
        if (resetClassifying > 0)
        {
            _logger.LogInformation("Stale-claim sweep reset {Count} document(s) back to TextExtracted.", resetClassifying);
        }

        // Phase-5 (DA-040): generalize the sweep to the embedding stage — a doc stuck in
        // GeneratingEmbeddings (worker crashed mid-embed/upsert) self-heals back to Classified for
        // re-claim. Same audit-timestamp threshold, no ClaimedAt column.
        var resetGenerating = await embedder.RecoverStaleGeneratingEmbeddingsAsync(_staleThreshold, stoppingToken);
        if (resetGenerating > 0)
        {
            _logger.LogInformation("Stale-claim sweep reset {Count} document(s) back to Classified.", resetGenerating);
        }
    }

    /// <summary>
    /// Pass 1 — extraction. Selects the oldest <c>Queued</c> documents (FIFO) and processes each in
    /// its own DI scope. Per-doc try/catch so one poison document never aborts the batch or the loop.
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

    /// <summary>
    /// Pass 2 — classification (DA-033). Selects the oldest <c>TextExtracted</c> documents (FIFO) and
    /// classifies each in its own DI scope via <see cref="IClassificationService.ClassifyAsync"/>.
    /// <para>
    /// <b>Transient backoff:</b> if the previous classify hit a <see cref="ProcessingOutcome.Transient"/>
    /// (LLM unreachable / model missing) we skip this pass for <c>TransientBackoffTicks</c> ticks so a
    /// down LLM is not hammered every poll interval — the docs stay <c>TextExtracted</c> and are
    /// retried once the backoff elapses. The extraction pass (pass 1) keeps running throughout.
    /// On the FIRST Transient seen in a draining batch we set the backoff and STOP the batch (the
    /// LLM is down for all of them — no point trying the rest this tick).
    /// </para>
    /// </summary>
    private async Task DrainTextExtractedAsync(CancellationToken stoppingToken)
    {
        if (_classifyBackoffRemaining > 0)
        {
            _classifyBackoffRemaining--;
            _logger.LogDebug(
                "Classification pass backing off after a transient LLM fault; {Remaining} tick(s) remaining.",
                _classifyBackoffRemaining);
            return;
        }

        IReadOnlyList<Guid> ids;
        using (var selectionScope = _scopeFactory.CreateScope())
        {
            var documents = selectionScope.ServiceProvider
                .GetRequiredService<DocuPilot.Repository.Abstractions.IDocumentRepository>();
            ids = await documents.GetNextTextExtractedIdsAsync(MaxBatchPerTick, stoppingToken);
        }

        foreach (var id in ids)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var outcome = await ClassifyOneAsync(id, stoppingToken);
                if (outcome == ProcessingOutcome.Transient)
                {
                    // The LLM is down/model-missing: arm the backoff and abandon the rest of this
                    // batch — they would all hit the same down LLM. Docs stay TextExtracted.
                    _classifyBackoffRemaining = _transientBackoffTicks;
                    _logger.LogWarning(
                        "Classification of document {DocumentId} was transient (LLM unreachable); " +
                        "leaving it TextExtracted and backing off the classification pass for {Ticks} tick(s).",
                        id, _transientBackoffTicks);
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // bubble up to the loop for a clean shutdown
            }
            catch (Exception ex)
            {
                // Isolation: an unexpected fault on one document logs and the loop continues.
                // The orchestrator converts content faults into a Failed terminal, so reaching
                // here means an infrastructure/transaction-level surprise.
                _logger.LogError(ex, "Unexpected error classifying document {DocumentId}; skipping it.", id);
            }
        }
    }

    /// <summary>
    /// Pass 3 — embedding (DA-040). Selects the oldest <c>Classified</c> documents (FIFO) and embeds
    /// each in its own DI scope via <see cref="IEmbeddingService.EmbedDocumentAsync"/>.
    /// <para>
    /// <b>Transient backoff:</b> mirrors pass 2 exactly. If a previous embed hit a
    /// <see cref="ProcessingOutcome.Transient"/> (embedder unreachable / Qdrant down) we skip this
    /// pass for <c>TransientBackoffTicks</c> ticks so a down dependency is not hammered every poll
    /// interval — the docs stay <c>Classified</c> and are retried once the backoff elapses. The
    /// extraction (pass 1) and classification (pass 2) passes keep running throughout. On the FIRST
    /// Transient seen in a draining batch we set the backoff and STOP the batch (the dependency is
    /// down for all of them — no point trying the rest this tick).
    /// </para>
    /// </summary>
    private async Task DrainClassifiedAsync(CancellationToken stoppingToken)
    {
        if (_embedBackoffRemaining > 0)
        {
            _embedBackoffRemaining--;
            _logger.LogDebug(
                "Embedding pass backing off after a transient embedder/Qdrant fault; {Remaining} tick(s) remaining.",
                _embedBackoffRemaining);
            return;
        }

        IReadOnlyList<Guid> ids;
        using (var selectionScope = _scopeFactory.CreateScope())
        {
            var documents = selectionScope.ServiceProvider
                .GetRequiredService<DocuPilot.Repository.Abstractions.IDocumentRepository>();
            ids = await documents.GetNextClassifiedIdsAsync(MaxBatchPerTick, stoppingToken);
        }

        foreach (var id in ids)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var outcome = await EmbedOneAsync(id, stoppingToken);
                if (outcome == ProcessingOutcome.Transient)
                {
                    // The embedder/Qdrant is down: arm the backoff and abandon the rest of this
                    // batch — they would all hit the same down dependency. Docs stay Classified.
                    _embedBackoffRemaining = _transientBackoffTicks;
                    _logger.LogWarning(
                        "Embedding of document {DocumentId} was transient (embedder/Qdrant unreachable); " +
                        "leaving it Classified and backing off the embedding pass for {Ticks} tick(s).",
                        id, _transientBackoffTicks);
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // bubble up to the loop for a clean shutdown
            }
            catch (Exception ex)
            {
                // Isolation: an unexpected fault on one document logs and the loop continues.
                // The orchestrator converts content faults into a Failed terminal, so reaching
                // here means an infrastructure/transaction-level surprise.
                _logger.LogError(ex, "Unexpected error embedding document {DocumentId}; skipping it.", id);
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

    /// <summary>
    /// Classifies a single document inside its own scope. Returns the outcome so the batch can arm
    /// the Transient backoff. <c>Transient</c> is intentionally NOT a failure — the doc is left
    /// <c>TextExtracted</c> by the orchestrator and retried after the backoff (DA-032 §6).
    /// </summary>
    private async Task<ProcessingOutcome> ClassifyOneAsync(Guid id, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var classifier = scope.ServiceProvider.GetRequiredService<IClassificationService>();

        var outcome = await classifier.ClassifyAsync(id, stoppingToken);
        switch (outcome)
        {
            case ProcessingOutcome.Succeeded:
                _logger.LogInformation("Document {DocumentId} classified: category + metadata persisted.", id);
                break;
            case ProcessingOutcome.Failed:
                _logger.LogWarning("Document {DocumentId} classification failed (marked Failed with a reason).", id);
                break;
            case ProcessingOutcome.Transient:
                // Handled by the caller (backoff); logged there with the backoff detail.
                break;
            case ProcessingOutcome.NotClaimed:
                // Another iteration/worker won the claim, or the row left TextExtracted between
                // selection and claim. Expected and benign — just move on.
                _logger.LogDebug("Document {DocumentId} was already claimed for classification; skipping.", id);
                break;
            case ProcessingOutcome.NotFound:
                _logger.LogDebug("Document {DocumentId} no longer exists; skipping.", id);
                break;
        }

        return outcome;
    }

    /// <summary>
    /// Embeds a single document inside its own scope. Returns the outcome so the batch can arm the
    /// Transient backoff. <c>Transient</c> is intentionally NOT a failure — the doc is left
    /// <c>Classified</c> by the orchestrator and retried after the backoff (DA-039 contract / ADR §6).
    /// </summary>
    private async Task<ProcessingOutcome> EmbedOneAsync(Guid id, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        var outcome = await embedder.EmbedDocumentAsync(id, stoppingToken);
        switch (outcome)
        {
            case ProcessingOutcome.Succeeded:
                _logger.LogInformation("Document {DocumentId} embedded: chunks + vectors persisted; ready for search.", id);
                break;
            case ProcessingOutcome.Failed:
                _logger.LogWarning("Document {DocumentId} embedding failed (marked Failed with a reason).", id);
                break;
            case ProcessingOutcome.Transient:
                // Handled by the caller (backoff); logged there with the backoff detail.
                break;
            case ProcessingOutcome.NotClaimed:
                // Another iteration/worker won the claim, or the row left Classified between
                // selection and claim. Expected and benign — just move on.
                _logger.LogDebug("Document {DocumentId} was already claimed for embedding; skipping.", id);
                break;
            case ProcessingOutcome.NotFound:
                _logger.LogDebug("Document {DocumentId} no longer exists; skipping.", id);
                break;
        }

        return outcome;
    }
}
