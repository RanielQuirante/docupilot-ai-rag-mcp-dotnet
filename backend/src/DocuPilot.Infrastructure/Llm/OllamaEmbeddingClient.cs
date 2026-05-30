using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuPilot.Infrastructure.Llm;

/// <summary>
/// HTTP <see cref="IEmbeddingClient"/> implementation targeting an in-network Ollama (or any
/// OpenAI-compatible / vLLM) embedding server, registered as a typed <c>HttpClient</c> (ADR §2). A
/// DEDICATED client distinct from <c>OllamaLlmClient</c> (which is left untouched) — embedding is a
/// different operation, model, and endpoint. Supports two wire styles selected by
/// <c>Embedding:ApiStyle</c>: <c>ollama-native</c> (<c>POST /api/embeddings</c> with a single
/// <c>prompt</c>, response <c>embedding</c>) and <c>openai</c> (<c>POST /v1/embeddings</c> with
/// <c>input</c>, response <c>data[0].embedding</c>) — vLLM is a pure config swap.
/// <para>
/// Fault mapping (ADR §6): connection-level failures (server down / DNS / refused), socket timeout,
/// HTTP 404 / "model not found" → mapped to <see cref="EmbeddingUnavailableException"/> so the
/// orchestrator treats them as TRANSIENT and leaves the document <c>Classified</c>.
/// </para>
/// </summary>
public sealed class OllamaEmbeddingClient : IEmbeddingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<OllamaEmbeddingClient> _logger;

    public OllamaEmbeddingClient(HttpClient httpClient, IOptions<EmbeddingOptions> options, ILogger<OllamaEmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public int Dimensions => _options.Dimensions;

    public async Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct)
    {
        var isOpenAi = string.Equals(_options.ApiStyle, EmbeddingOptions.ApiStyleOpenAi, StringComparison.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var vector = isOpenAi
                ? await EmbedOpenAiAsync(text, ct)
                : await EmbedOllamaNativeAsync(text, ct);

            if (vector.Length == 0)
            {
                throw new InvalidOperationException("Embedding server returned an empty vector.");
            }

            return new EmbeddingResult(vector, _options.Model, stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            // Connection-level fault (server down / refused / DNS) — TRANSIENT (ADR §6).
            throw new EmbeddingUnavailableException(
                $"Embedding server unreachable at {_options.BaseUrl} ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient socket timeout (not the caller's CT) — transient connectivity fault.
            throw new EmbeddingUnavailableException(
                $"Embedding request timed out contacting {_options.BaseUrl}.", ex);
        }
    }

    // ---- Ollama native: POST /api/embeddings { model, prompt } → { embedding: [...] } ----
    private async Task<float[]> EmbedOllamaNativeAsync(string text, CancellationToken ct)
    {
        var payload = new OllamaEmbeddingsRequest { Model = _options.Model, Prompt = text };

        using var response = await _httpClient.PostAsJsonAsync("/api/embeddings", payload, JsonOptions, ct);
        await EnsureSuccessOrMapAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<OllamaEmbeddingsResponse>(JsonOptions, ct);
        return body?.Embedding ?? [];
    }

    // ---- OpenAI-compatible: POST /v1/embeddings { model, input } → { data: [ { embedding: [...] } ] } ----
    private async Task<float[]> EmbedOpenAiAsync(string text, CancellationToken ct)
    {
        var payload = new OpenAiEmbeddingsRequest { Model = _options.Model, Input = text };

        using var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", payload, JsonOptions, ct);
        await EnsureSuccessOrMapAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<OpenAiEmbeddingsResponse>(JsonOptions, ct);
        return body?.Data?.FirstOrDefault()?.Embedding ?? [];
    }

    /// <summary>
    /// Maps a non-success status. A 404 or a "model not found"-style body → transient
    /// <see cref="EmbeddingUnavailableException"/> (the model isn't pulled yet — leave the doc to
    /// retry); any other non-success → an <see cref="HttpRequestException"/> which
    /// <see cref="EmbedAsync"/> also maps to unavailable (embedding failures are always transient —
    /// there is no per-document "content fault" for a deterministic embedder).
    /// </summary>
    private async Task EnsureSuccessOrMapAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await SafeReadBodyAsync(response, ct);

        var modelMissing = response.StatusCode == HttpStatusCode.NotFound
            || body.Contains("model", StringComparison.OrdinalIgnoreCase)
                && (body.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("try pulling", StringComparison.OrdinalIgnoreCase));

        if (modelMissing)
        {
            _logger.LogWarning("Embedding model '{Model}' appears unavailable (HTTP {Status}): {Body}",
                _options.Model, (int)response.StatusCode, body);
            throw new EmbeddingUnavailableException(
                $"Embedding model '{_options.Model}' not available (HTTP {(int)response.StatusCode}). It may not be pulled yet.");
        }

        throw new HttpRequestException(
            $"Embedding call failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---- wire DTOs ----

    private sealed class OllamaEmbeddingsRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty;
    }

    private sealed class OllamaEmbeddingsResponse
    {
        [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
    }

    private sealed class OpenAiEmbeddingsRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("input")] public string Input { get; set; } = string.Empty;
    }

    private sealed class OpenAiEmbeddingsResponse
    {
        [JsonPropertyName("data")] public List<OpenAiEmbeddingDatum>? Data { get; set; }
    }

    private sealed class OpenAiEmbeddingDatum
    {
        [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
    }
}
