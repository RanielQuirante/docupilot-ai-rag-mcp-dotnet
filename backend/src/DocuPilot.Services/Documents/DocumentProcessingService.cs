using System.Diagnostics;
using System.Text.Json;
using DocuPilot.Models.Entities;
using DocuPilot.Models.Enums;
using DocuPilot.Repository.Abstractions;
using DocuPilot.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuPilot.Services.Documents;

/// <summary>
/// Processing orchestrator owning the Phase-3 state machine (ADR §2/§5/§6, DA-023 §P3.8).
/// Reusable by the API (manual trigger) and the Worker (DA-025): the Worker's BackgroundService
/// just loops + delegates to <see cref="ProcessAsync"/>. Loads the file via
/// <see cref="IFileStorage"/>, extracts text under a timeout with bounded transient retry,
/// caps the text length, then commits the terminal transition + text upsert + audit row in a
/// single transaction so the audit trail can never drift from the actual status.
/// </summary>
public sealed class DocumentProcessingService : IDocumentProcessingService
{
    private const string DocumentEntityName = "Document";

    private readonly IDocumentRepository _documents;
    private readonly IDocumentTextRepository _texts;
    private readonly IAuditRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorage _fileStorage;
    private readonly ITextExtractionService _extraction;
    private readonly TimeProvider _timeProvider;
    private readonly ExtractionOptions _options;
    private readonly ILogger<DocumentProcessingService> _logger;

    public DocumentProcessingService(
        IDocumentRepository documents,
        IDocumentTextRepository texts,
        IAuditRepository audit,
        IUnitOfWork unitOfWork,
        IFileStorage fileStorage,
        ITextExtractionService extraction,
        TimeProvider timeProvider,
        IOptions<ExtractionOptions> options,
        ILogger<DocumentProcessingService> logger)
    {
        _documents = documents;
        _texts = texts;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _fileStorage = fileStorage;
        _extraction = extraction;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ProcessingOutcome> ProcessAsync(Guid documentId, CancellationToken ct)
    {
        var document = await _documents.GetByIdAsync(documentId, ct);
        if (document is null)
        {
            return ProcessingOutcome.NotFound;
        }

        // Atomic claim: Queued → ExtractingText (compare-and-swap). If we lose the claim
        // (already ExtractingText/terminal, or another worker won), don't process.
        var claimed = await _documents.TryClaimAsync(documentId, ct);
        if (!claimed)
        {
            _logger.LogDebug("Document {DocumentId} was not claimable (status {Status}).", documentId, document.Status);
            return ProcessingOutcome.NotClaimed;
        }

        // The ExtractionStarted audit is written immediately after a successful claim, in its
        // own commit — we do NOT hold a transaction open for the duration of extraction.
        await WriteAuditAsync(documentId, AuditAction.ExtractionStarted,
            JsonSerializer.Serialize(new { fromStatus = nameof(DocumentStatus.Queued), toStatus = nameof(DocumentStatus.ExtractingText) }), ct);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var text = await ExtractWithRetryAsync(document, ct);

            var truncated = false;
            if (text.Length > _options.MaxTextChars)
            {
                text = text[.._options.MaxTextChars];
                truncated = true;
            }

            await CompleteSuccessAsync(document, text, truncated, stopwatch.ElapsedMilliseconds, ct);
            return ProcessingOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown — leave the document in ExtractingText for stale-claim recovery
            // (DA-025) to reclaim. Do NOT mark Failed on a cooperative shutdown.
            throw;
        }
        catch (Exception ex)
        {
            var reason = ToFailureReason(ex);
            _logger.LogWarning(ex, "Extraction failed for document {DocumentId}: {Reason}", documentId, reason);
            await CompleteFailureAsync(document, reason, ex, stopwatch.ElapsedMilliseconds, ct);
            return ProcessingOutcome.Failed;
        }
    }

    public async Task<RequeueResult> RequeueAsync(Guid documentId, CancellationToken ct)
    {
        var document = await _documents.GetByIdAsync(documentId, ct);
        if (document is null)
        {
            return RequeueResult.NotFound;
        }

        // Already in-flight — cannot re-queue (409). Includes the Phase-4 in-progress claim
        // (Classifying) so a re-process can't race an active classification.
        if (document.Status is DocumentStatus.Queued or DocumentStatus.ExtractingText or DocumentStatus.Classifying)
        {
            return RequeueResult.Conflict;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            var fromStatus = document.Status;
            document.Status = DocumentStatus.Queued;
            document.FailureReason = null;
            document.ProcessedAt = null;
            await _documents.SaveChangesAsync(innerCt);

            await _audit.AddAsync(BuildAudit(documentId, AuditAction.ReprocessQueued, now,
                JsonSerializer.Serialize(new { fromStatus = fromStatus.ToString(), toStatus = nameof(DocumentStatus.Queued) })), innerCt);
        }, ct);

        return RequeueResult.Queued;
    }

    public async Task<int> RecoverStaleClaimsAsync(TimeSpan staleThreshold, CancellationToken ct)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - staleThreshold;
        var staleIds = await _documents.GetStaleExtractingIdsAsync(cutoff, ct);
        if (staleIds.Count == 0)
        {
            return 0;
        }

        var reset = 0;
        foreach (var id in staleIds)
        {
            ct.ThrowIfCancellationRequested();

            var document = await _documents.GetByIdAsync(id, ct);
            // Re-check under the tracked load — another iteration/worker may have moved it on.
            if (document is null || document.Status != DocumentStatus.ExtractingText)
            {
                continue;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
            {
                document.Status = DocumentStatus.Queued;
                await _documents.SaveChangesAsync(innerCt);

                await _audit.AddAsync(BuildAudit(id, AuditAction.ReprocessQueued, now,
                    JsonSerializer.Serialize(new
                    {
                        fromStatus = nameof(DocumentStatus.ExtractingText),
                        toStatus = nameof(DocumentStatus.Queued),
                        reason = "stale-claim recovery",
                    })), innerCt);
            }, ct);

            _logger.LogWarning("Reset stale claim for document {DocumentId} (stuck in ExtractingText past threshold) back to Queued.", id);
            reset++;
        }

        return reset;
    }

    /// <summary>
    /// Runs extraction under a per-document timeout with bounded retry for TRANSIENT faults
    /// only (I/O hiccup / timeout). Non-transient faults (unsupported/empty) fail fast.
    /// </summary>
    private async Task<string> ExtractWithRetryAsync(Document document, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await using var stream = await _fileStorage.OpenReadAsync(document.FilePath, linkedCts.Token);
                var text = await _extraction.ExtractAsync(stream, document.ContentType, document.FileName, linkedCts.Token);

                if (string.IsNullOrWhiteSpace(text))
                {
                    // Non-transient: empty/image-only → fail fast (PM Q3), no retry.
                    throw new EmptyExtractionException(
                        "No extractable text (the document may be image-only/scanned).");
                }

                return text;
            }
            catch (UnsupportedFormatException)
            {
                throw; // non-transient
            }
            catch (EmptyExtractionException)
            {
                throw; // non-transient
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // host shutdown — propagate, do not retry
            }
            catch (OperationCanceledException)
            {
                // Timeout (timeoutCts fired, not the host token) — transient.
                lastTransient = new TimeoutException($"Extraction timed out after {_options.TimeoutSeconds}s.");
            }
            catch (Exception ex)
            {
                // I/O or library hiccup — treat as transient and retry.
                lastTransient = ex;
            }

            if (attempt < maxAttempts)
            {
                // Short backoff: 0.5s, then growing. Honors the host token for fast shutdown.
                var delayMs = 500 * attempt;
                await Task.Delay(delayMs, ct);
            }
        }

        throw lastTransient ?? new InvalidOperationException("Extraction failed for an unknown reason.");
    }

    private async Task CompleteSuccessAsync(Document document, string text, bool truncated, long durationMs, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            // Upsert the 1:1 text row by DocumentId (idempotent — DA-023 §P3.2.2).
            await _texts.UpsertAsync(new DocumentText
            {
                Id = Guid.CreateVersion7(),
                DocumentId = document.Id,
                Content = text,
                CharCount = text.Length,
                ExtractedAt = now,
            }, innerCt);

            // Advance status + stamp ProcessedAt + clear any prior FailureReason.
            document.Status = DocumentStatus.TextExtracted;
            document.ProcessedAt = now;
            document.FailureReason = null;
            await _documents.SaveChangesAsync(innerCt);

            await _audit.AddAsync(BuildAudit(document.Id, AuditAction.ExtractionSucceeded, now,
                JsonSerializer.Serialize(new
                {
                    fromStatus = nameof(DocumentStatus.ExtractingText),
                    toStatus = nameof(DocumentStatus.TextExtracted),
                    charCount = text.Length,
                    truncated,
                    durationMs,
                })), innerCt);
        }, ct);
    }

    private async Task CompleteFailureAsync(Document document, string reason, Exception ex, long durationMs, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            document.Status = DocumentStatus.Failed;
            document.ProcessedAt = now;
            document.FailureReason = reason;
            await _documents.SaveChangesAsync(innerCt);

            await _audit.AddAsync(BuildAudit(document.Id, AuditAction.ExtractionFailed, now,
                JsonSerializer.Serialize(new
                {
                    fromStatus = nameof(DocumentStatus.ExtractingText),
                    toStatus = nameof(DocumentStatus.Failed),
                    error = reason,
                    exceptionType = ex.GetType().Name,
                    durationMs,
                })), innerCt);
        }, ct);
    }

    /// <summary>Writes a single audit row in its own committed transaction.</summary>
    private async Task WriteAuditAsync(Guid entityId, AuditAction action, string? detailsJson, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            await _audit.AddAsync(BuildAudit(entityId, action, now, detailsJson), innerCt);
        }, ct);
    }

    private static AuditLog BuildAudit(Guid entityId, AuditAction action, DateTime createdAt, string? detailsJson) => new()
    {
        Id = Guid.CreateVersion7(),
        EntityName = DocumentEntityName,
        EntityId = entityId,
        Action = action.ToString(),
        DetailsJson = detailsJson,
        CreatedAt = createdAt,
    };

    private static string ToFailureReason(Exception ex) => ex switch
    {
        UnsupportedFormatException => ex.Message,
        EmptyExtractionException => ex.Message,
        TimeoutException => ex.Message,
        _ => $"Extraction error: {ex.Message}",
    };
}
