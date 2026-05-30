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
/// HTTP <see cref="ILlmClient"/> implementation targeting an in-network Ollama (or any
/// OpenAI-compatible / vLLM) server, registered as a typed <c>HttpClient</c> (ADR §1). Supports
/// two wire styles selected by <c>Llm:ApiStyle</c>: <c>ollama-native</c> (<c>/api/generate</c> +
/// <c>format:"json"</c>) and <c>openai</c> (<c>/v1/chat/completions</c> +
/// <c>response_format:{type:"json_object"}</c>) — vLLM is a pure config swap, no second class.
/// Sets <c>temperature</c> (default 0) and JSON-mode per request.
/// <para>
/// Fault mapping (ADR §6): connection-level failures (server down / DNS / refused) and
/// "model not found" (HTTP 404 / Ollama's error body) are mapped to
/// <see cref="LlmUnavailableException"/> so the orchestrator treats them as TRANSIENT and leaves
/// the document <c>TextExtracted</c> (no backlog poisoning). Other HTTP errors and empty bodies
/// throw ordinary exceptions the orchestrator treats as content/parse faults.
/// </para>
/// </summary>
public sealed class OllamaLlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;
    private readonly ILogger<OllamaLlmClient> _logger;

    public OllamaLlmClient(HttpClient httpClient, IOptions<LlmOptions> options, ILogger<OllamaLlmClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var isOpenAi = string.Equals(_options.ApiStyle, LlmOptions.ApiStyleOpenAi, StringComparison.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            return isOpenAi
                ? await CompleteOpenAiAsync(request, stopwatch, ct)
                : await CompleteOllamaNativeAsync(request, stopwatch, ct);
        }
        catch (HttpRequestException ex)
        {
            // Connection-level fault (server down / refused / DNS) — TRANSIENT (ADR §6).
            throw new LlmUnavailableException(
                $"LLM server unreachable at {_options.BaseUrl} ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout (not the caller's CT) — the orchestrator's per-call timeout wraps
            // this separately; treat a raw socket timeout here as a transient connectivity fault.
            throw new LlmUnavailableException(
                $"LLM request timed out contacting {_options.BaseUrl}.", ex);
        }
    }

    // ---- Ollama native: POST /api/generate { model, prompt, system, format:"json", stream:false, options:{temperature} } ----
    private async Task<LlmResponse> CompleteOllamaNativeAsync(LlmRequest request, Stopwatch stopwatch, CancellationToken ct)
    {
        var payload = new OllamaGenerateRequest
        {
            Model = _options.Model,
            Prompt = request.Prompt,
            System = request.System,
            Format = request.JsonMode ? "json" : null,
            Stream = false,
            Options = new OllamaOptions { Temperature = request.Temperature },
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/generate", payload, JsonOptions, ct);
        await EnsureSuccessOrMapAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(JsonOptions, ct);
        var content = body?.Response;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("LLM returned an empty response body.");
        }

        return new LlmResponse(content, _options.Model, stopwatch.Elapsed);
    }

    // ---- OpenAI-compatible: POST /v1/chat/completions { model, messages[], response_format, temperature } ----
    private async Task<LlmResponse> CompleteOpenAiAsync(LlmRequest request, Stopwatch stopwatch, CancellationToken ct)
    {
        var messages = new List<OpenAiMessage>();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(new OpenAiMessage { Role = "system", Content = request.System });
        }

        messages.Add(new OpenAiMessage { Role = "user", Content = request.Prompt });

        var payload = new OpenAiChatRequest
        {
            Model = _options.Model,
            Messages = messages,
            Temperature = request.Temperature,
            ResponseFormat = request.JsonMode ? new OpenAiResponseFormat { Type = "json_object" } : null,
        };

        using var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", payload, JsonOptions, ct);
        await EnsureSuccessOrMapAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(JsonOptions, ct);
        var content = body?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("LLM returned an empty response body.");
        }

        return new LlmResponse(content, _options.Model, stopwatch.Elapsed);
    }

    /// <summary>
    /// Maps a non-success status. A 404 or a "model not found"-style body → transient
    /// <see cref="LlmUnavailableException"/> (the model isn't loaded yet — leave the doc to retry);
    /// any other non-success → an ordinary exception (content/upstream fault).
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
            _logger.LogWarning("LLM model '{Model}' appears unavailable (HTTP {Status}): {Body}",
                _options.Model, (int)response.StatusCode, body);
            throw new LlmUnavailableException(
                $"LLM model '{_options.Model}' not available (HTTP {(int)response.StatusCode}). It may not be pulled yet.");
        }

        throw new HttpRequestException(
            $"LLM call failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
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

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty;
        [JsonPropertyName("system")] public string? System { get; set; }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
    }

    private sealed class OllamaOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
    }

    private sealed class OpenAiChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public List<OpenAiMessage> Messages { get; set; } = [];
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("response_format")] public OpenAiResponseFormat? ResponseFormat { get; set; }
    }

    private sealed class OpenAiResponseFormat
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "json_object";
    }

    private sealed class OpenAiMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private sealed class OpenAiChatResponse
    {
        [JsonPropertyName("choices")] public List<OpenAiChoice>? Choices { get; set; }
    }

    private sealed class OpenAiChoice
    {
        [JsonPropertyName("message")] public OpenAiMessage? Message { get; set; }
    }
}
