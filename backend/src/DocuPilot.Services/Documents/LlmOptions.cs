namespace DocuPilot.Services.Documents;

/// <summary>
/// LLM client + classification bounds, bound from the <c>Llm</c> config section (env keys
/// <c>Llm__BaseUrl</c> / <c>Llm__Model</c> / <c>Llm__ApiStyle</c> / <c>Llm__TimeoutSeconds</c> /
/// <c>Llm__MaxAttempts</c> / <c>Llm__MaxInputChars</c> / <c>Llm__Temperature</c>). Defaults per
/// ADR §1/§6 + PM decisions. DevOps (DA-036) wires these on api + worker and documents them in
/// <c>.env.example</c>. Lives in Services (not Infrastructure) so the orchestrator and the
/// OllamaLlmClient bind the same options.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Llm";

    /// <summary>Ollama-native HTTP API style (<c>/api/generate</c> + <c>format:json</c>).</summary>
    public const string ApiStyleOllamaNative = "ollama-native";

    /// <summary>OpenAI-compatible HTTP API style (<c>/v1/chat/completions</c> + <c>response_format</c>) — vLLM swap.</summary>
    public const string ApiStyleOpenAi = "openai";

    /// <summary>In-network base URL of the LLM server. Default <c>http://ollama:11434</c> (compose service + container port).</summary>
    public string BaseUrl { get; set; } = "http://ollama:11434";

    /// <summary>Default model name. Default <c>llama3.2:3b</c> (QA cheap-gate may set <c>llama3.2:1b</c>).</summary>
    public string Model { get; set; } = "llama3.2:3b";

    /// <summary>API style: <c>ollama-native</c> (default) or <c>openai</c> (vLLM-compat — config swap, no second class).</summary>
    public string ApiStyle { get; set; } = ApiStyleOllamaNative;

    /// <summary>Per-call timeout in seconds. Default 120 (CPU-only small models are slow; far longer than extraction).</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Max attempts per LLM call; only transient HTTP/timeout/parse faults are retried. Default 2.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Max characters of document text fed to the prompt; text beyond this is truncated (head-first). Default 12,000.</summary>
    public int MaxInputChars { get; set; } = 12_000;

    /// <summary>Sampling temperature for classification + metadata. Default 0 (greedy) for reproducibility.</summary>
    public double Temperature { get; set; } = 0;
}
