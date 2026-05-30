namespace DocuPilot.Services.Documents;

/// <summary>
/// Embedding client bounds, bound from the <c>Embedding</c> config section (env keys
/// <c>Embedding__BaseUrl</c> / <c>Embedding__Model</c> / <c>Embedding__Dimensions</c> /
/// <c>Embedding__ApiStyle</c> / <c>Embedding__TimeoutSeconds</c> / <c>Embedding__MaxAttempts</c>).
/// A NEW section mirroring <c>Llm:*</c> — it does NOT reuse it (the embedding model + base URL are
/// genuinely independent of the chat model, ADR §2). Defaults per ADR §2: <c>nomic-embed-text</c>,
/// 768 dimensions, ollama-native. Lives in Services (not Infrastructure) so the orchestrator and
/// the <c>OllamaEmbeddingClient</c> bind the same options. DevOps (DA-043) wires these on api +
/// worker and documents them in <c>.env.example</c>.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Embedding";

    /// <summary>Ollama-native HTTP API style (<c>POST /api/embeddings</c> with a single <c>prompt</c>).</summary>
    public const string ApiStyleOllamaNative = "ollama-native";

    /// <summary>OpenAI-compatible HTTP API style (<c>POST /v1/embeddings</c> with <c>input</c>) — vLLM / OpenAI swap.</summary>
    public const string ApiStyleOpenAi = "openai";

    /// <summary>In-network base URL of the embedding server. Default <c>http://ollama:11434</c> (same Ollama hosts chat + embedding models).</summary>
    public string BaseUrl { get; set; } = "http://ollama:11434";

    /// <summary>Embedding model name. Default <c>nomic-embed-text</c> (768-dim). Swap ⇒ MUST recreate the collection at the new dim (the bootstrap dim-validate guards it).</summary>
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>Vector size cross-check; MUST equal the model's true dim AND the Qdrant collection's size. Default 768.</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>API style: <c>ollama-native</c> (default) or <c>openai</c> (vLLM / OpenAI-compat — config swap, no second class).</summary>
    public string ApiStyle { get; set; } = ApiStyleOllamaNative;

    /// <summary>Per-call timeout in seconds. Default 60 (embedding is far faster than chat). HttpClient timeout = this + buffer.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Max attempts per embedding call; only transient HTTP/timeout faults are retried. Default 3 (more than chat — embedding is cheap to retry).</summary>
    public int MaxAttempts { get; set; } = 3;
}
