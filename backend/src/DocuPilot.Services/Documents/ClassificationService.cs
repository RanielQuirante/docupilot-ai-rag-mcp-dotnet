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
/// Phase-4 classification + metadata orchestrator (ADR §2 option B / §3–§6). Reusable by the
/// Worker (DA-033) and any manual trigger: it claims the <c>TextExtracted</c> document internally
/// (atomic <c>TextExtracted → Classifying</c> CAS — we do NOT hold a DB transaction across the
/// slow LLM round-trips), calls the LLM to classify then to extract metadata, validates/coerces
/// both, and commits classification + metadata + status + audit in ONE transaction.
/// <para>
/// LLM-down vs content-fault are made explicit in the return outcome: an
/// <see cref="LlmUnavailableException"/> rolls the claim back to <c>TextExtracted</c> and returns
/// <see cref="ProcessingOutcome.Transient"/> (retry later, no backlog poisoning); an unparseable
/// classification or missing text sets <c>Failed</c> + <c>FailureReason</c> and returns
/// <see cref="ProcessingOutcome.Failed"/>; metadata structural failure degrades to <c>"{}"</c> and
/// the document still <c>Classified</c> (metadata never fails the doc alone).
/// </para>
/// </summary>
public sealed class ClassificationService : IClassificationService
{
    private const string DocumentEntityName = "Document";
    private const int MaxReasonLength = 1000;

    private static readonly JsonSerializerOptions JsonReadOptions = new(JsonSerializerDefaults.Web);

    private const string ClassificationSystemPrompt =
        "You are an enterprise document classification assistant. Respond with a single JSON object only.";

    private const string MetadataSystemPrompt =
        "You are an enterprise document metadata extraction assistant. Respond with a single JSON object only.";

    private readonly IDocumentRepository _documents;
    private readonly IDocumentTextRepository _texts;
    private readonly IDocumentClassificationRepository _classifications;
    private readonly IExtractedMetadataRepository _metadata;
    private readonly IAuditRepository _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILlmClient _llm;
    private readonly IPromptProvider _prompts;
    private readonly TimeProvider _timeProvider;
    private readonly LlmOptions _options;
    private readonly ILogger<ClassificationService> _logger;

    public ClassificationService(
        IDocumentRepository documents,
        IDocumentTextRepository texts,
        IDocumentClassificationRepository classifications,
        IExtractedMetadataRepository metadata,
        IAuditRepository audit,
        IUnitOfWork unitOfWork,
        ILlmClient llm,
        IPromptProvider prompts,
        TimeProvider timeProvider,
        IOptions<LlmOptions> options,
        ILogger<ClassificationService> logger)
    {
        _documents = documents;
        _texts = texts;
        _classifications = classifications;
        _metadata = metadata;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _llm = llm;
        _prompts = prompts;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ProcessingOutcome> ClassifyAsync(Guid documentId, CancellationToken ct)
    {
        var document = await _documents.GetByIdAsync(documentId, ct);
        if (document is null)
        {
            return ProcessingOutcome.NotFound;
        }

        // Atomic claim: TextExtracted → Classifying (compare-and-swap). Lose → don't process.
        var claimed = await _documents.TryClaimForClassificationAsync(documentId, ct);
        if (!claimed)
        {
            _logger.LogDebug("Document {DocumentId} was not claimable for classification (status {Status}).", documentId, document.Status);
            return ProcessingOutcome.NotClaimed;
        }

        // The tracked entity still reflects the pre-claim status; align it so persistence reasons
        // from the claimed state.
        document.Status = DocumentStatus.Classifying;

        await WriteAuditAsync(documentId, AuditAction.ClassificationStarted,
            JsonSerializer.Serialize(new { fromStatus = nameof(DocumentStatus.TextExtracted), toStatus = nameof(DocumentStatus.Classifying) }), ct);

        // Content fault: no extracted text to classify → Failed (not transient).
        var text = await _texts.GetByDocumentIdAsync(documentId, ct);
        if (text is null || string.IsNullOrWhiteSpace(text.Content))
        {
            await CompleteFailureAsync(document, "No extracted text available to classify.", ct);
            return ProcessingOutcome.Failed;
        }

        var input = Truncate(text.Content, _options.MaxInputChars);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var classification = await ClassifyWithRetryAsync(input, ct);
            var metadataJson = await ExtractMetadataAsync(DocumentCategoryNames.ToDisplay(classification.Category), input, ct);

            await CompleteSuccessAsync(document, classification, metadataJson, stopwatch.ElapsedMilliseconds, ct);
            return ProcessingOutcome.Succeeded;
        }
        catch (LlmUnavailableException ex)
        {
            // TRANSIENT: the LLM service/model is unavailable. Roll the claim back to
            // TextExtracted (NO Failed) so the Worker retries it later (ADR §6 / PM Q3).
            _logger.LogWarning(ex, "LLM unavailable while classifying {DocumentId}; leaving document TextExtracted for retry.", documentId);
            await RollbackClaimAsync(document, ct);
            return ProcessingOutcome.Transient;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown — leave the document Classifying for stale-claim recovery (DA-033).
            throw;
        }
        catch (ClassificationContentException ex)
        {
            _logger.LogWarning("Classification content fault for {DocumentId}: {Reason}", documentId, ex.Message);
            await CompleteFailureAsync(document, ex.Message, ct);
            return ProcessingOutcome.Failed;
        }
    }

    public async Task<int> RecoverStaleClassifyingAsync(TimeSpan staleThreshold, CancellationToken ct)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - staleThreshold;
        var staleIds = await _documents.GetStaleClassifyingIdsAsync(cutoff, ct);
        if (staleIds.Count == 0)
        {
            return 0;
        }

        var reset = 0;
        foreach (var id in staleIds)
        {
            ct.ThrowIfCancellationRequested();

            var document = await _documents.GetByIdAsync(id, ct);
            if (document is null || document.Status != DocumentStatus.Classifying)
            {
                continue;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
            {
                document.Status = DocumentStatus.TextExtracted;
                await _documents.SaveChangesAsync(innerCt);

                await _audit.AddAsync(BuildAudit(id, AuditAction.ReprocessQueued, now,
                    JsonSerializer.Serialize(new
                    {
                        fromStatus = nameof(DocumentStatus.Classifying),
                        toStatus = nameof(DocumentStatus.TextExtracted),
                        reason = "stale-claim recovery",
                    })), innerCt);
            }, ct);

            _logger.LogWarning("Reset stale claim for document {DocumentId} (stuck in Classifying past threshold) back to TextExtracted.", id);
            reset++;
        }

        return reset;
    }

    /// <summary>
    /// Calls the LLM to classify, with bounded retry for TRANSIENT faults (unparseable JSON). An
    /// <see cref="LlmUnavailableException"/> propagates immediately (transient at a higher level —
    /// the doc stays TextExtracted). An unparseable response after exhaustion → content fault.
    /// </summary>
    private async Task<ClassificationOutcome> ClassifyWithRetryAsync(string input, CancellationToken ct)
    {
        var prompt = _prompts.BuildClassificationPrompt(input);
        var maxAttempts = Math.Max(1, _options.MaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await CallLlmAsync(ClassificationSystemPrompt, prompt, ct);

            if (TryParseClassification(response.Content, out var parsed))
            {
                return parsed;
            }

            _logger.LogWarning("Classifier returned unparseable JSON (attempt {Attempt}/{Max}).", attempt, maxAttempts);
            if (attempt < maxAttempts)
            {
                await Task.Delay(500 * attempt, ct);
            }
        }

        throw new ClassificationContentException("Classifier returned an unparseable response.");
    }

    /// <summary>
    /// Calls the LLM to extract metadata (best-effort): a non-object / unparseable response
    /// degrades to <c>"{}"</c> (never fails the doc). An <see cref="LlmUnavailableException"/>
    /// still propagates (transient — leave TextExtracted).
    /// </summary>
    private async Task<string> ExtractMetadataAsync(string classification, string input, CancellationToken ct)
    {
        var prompt = _prompts.BuildMetadataPrompt(classification, input);
        var response = await CallLlmAsync(MetadataSystemPrompt, prompt, ct);

        var normalized = NormalizeJsonObject(response.Content);
        if (normalized is null)
        {
            _logger.LogWarning("Metadata response was not a JSON object; storing empty object {{}}.");
            return "{}";
        }

        return normalized;
    }

    /// <summary>Single LLM call under a per-call timeout linked to the host token (ADR §6).</summary>
    private async Task<LlmResponse> CallLlmAsync(string system, string prompt, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await _llm.CompleteAsync(
                new LlmRequest(prompt, system, JsonMode: true, Temperature: _options.Temperature),
                linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host shutdown — propagate
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Per-call timeout — treat as transient unavailability (leave TextExtracted to retry).
            throw new LlmUnavailableException($"LLM call timed out after {_options.TimeoutSeconds}s.");
        }
    }

    // ---- parsing / validation ----

    private static bool TryParseClassification(string content, out ClassificationOutcome result)
    {
        result = default;
        var json = ExtractJsonObject(content);
        if (json is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // classification: coerce off-taxonomy / null / empty → Unknown (does NOT fail parse).
            string? rawCategory = root.TryGetProperty("classification", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            var category = DocumentCategoryNames.Coerce(rawCategory);

            // confidence: clamp to [0,1]; missing/unparseable → 0.
            double confidence = 0;
            if (root.TryGetProperty("confidence", out var conf))
            {
                confidence = conf.ValueKind switch
                {
                    JsonValueKind.Number when conf.TryGetDouble(out var d) => d,
                    JsonValueKind.String when double.TryParse(conf.GetString(), out var d) => d,
                    _ => 0,
                };
            }

            confidence = Math.Clamp(confidence, 0d, 1d);

            string? reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            reason = Truncate(reason, MaxReasonLength);

            result = new ClassificationOutcome(category, (decimal)confidence, reason);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Returns the JSON object text if <paramref name="content"/> is a valid JSON object, else null (→ "{}").</summary>
    private static string? NormalizeJsonObject(string content)
    {
        var json = ExtractJsonObject(content);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Re-serialize compactly so what we store is canonical (also strips any prose wrapper).
            return JsonSerializer.Serialize(doc.RootElement, JsonReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the first balanced top-level JSON object out of a model response, tolerating prose or
    /// markdown fences around it (JSON-mode usually prevents this, but be defensive). Returns null
    /// if no object is found.
    /// </summary>
    private static string? ExtractJsonObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return content[start..(end + 1)];
    }

    // ---- persistence ----

    private async Task CompleteSuccessAsync(Document document, ClassificationOutcome classification, string metadataJson, long durationMs, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            await _classifications.UpsertAsync(new DocumentClassification
            {
                Id = Guid.CreateVersion7(),
                DocumentId = document.Id,
                Classification = classification.Category,
                Confidence = classification.Confidence,
                Reason = classification.Reason,
                Model = _options.Model,
                CreatedAt = now,
            }, innerCt);

            await _metadata.UpsertAsync(new ExtractedMetadata
            {
                Id = Guid.CreateVersion7(),
                DocumentId = document.Id,
                MetadataJson = metadataJson,
                Model = _options.Model,
                CreatedAt = now,
            }, innerCt);

            document.Status = DocumentStatus.Classified;
            document.ProcessedAt = now;
            document.FailureReason = null;
            await _documents.SaveChangesAsync(innerCt);

            await _audit.AddAsync(BuildAudit(document.Id, AuditAction.ClassificationSucceeded, now,
                JsonSerializer.Serialize(new
                {
                    fromStatus = nameof(DocumentStatus.Classifying),
                    toStatus = nameof(DocumentStatus.Classified),
                    classification = DocumentCategoryNames.ToDisplay(classification.Category),
                    confidence = classification.Confidence,
                    metadataChars = metadataJson.Length,
                    model = _options.Model,
                    durationMs,
                })), innerCt);
        }, ct);
    }

    private async Task CompleteFailureAsync(Document document, string reason, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            document.Status = DocumentStatus.Failed;
            document.ProcessedAt = now;
            document.FailureReason = reason;
            await _documents.SaveChangesAsync(innerCt);

            await _audit.AddAsync(BuildAudit(document.Id, AuditAction.ClassificationFailed, now,
                JsonSerializer.Serialize(new
                {
                    fromStatus = nameof(DocumentStatus.Classifying),
                    toStatus = nameof(DocumentStatus.Failed),
                    error = reason,
                })), innerCt);
        }, ct);
    }

    /// <summary>Transient LLM-down path: reset the claim Classifying → TextExtracted, with an audit row, no Failed.</summary>
    private async Task RollbackClaimAsync(Document document, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            document.Status = DocumentStatus.TextExtracted;
            await _documents.SaveChangesAsync(innerCt);

            await _audit.AddAsync(BuildAudit(document.Id, AuditAction.ReprocessQueued, now,
                JsonSerializer.Serialize(new
                {
                    fromStatus = nameof(DocumentStatus.Classifying),
                    toStatus = nameof(DocumentStatus.TextExtracted),
                    reason = "LLM unavailable — transient, retry later",
                })), innerCt);
        }, ct);
    }

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

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int max) =>
        value is not null && value.Length > max ? value[..max] : value;

    /// <summary>A validated classification result ready to persist.</summary>
    private readonly record struct ClassificationOutcome(DocumentCategory Category, decimal Confidence, string? Reason);

    /// <summary>A per-document content/parse fault (→ Failed). NOT used for LLM-unavailable (that's transient).</summary>
    private sealed class ClassificationContentException : Exception
    {
        public ClassificationContentException(string message)
            : base(message)
        {
        }
    }
}
