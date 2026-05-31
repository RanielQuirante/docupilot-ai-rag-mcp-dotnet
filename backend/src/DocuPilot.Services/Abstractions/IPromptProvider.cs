namespace DocuPilot.Services.Abstractions;

/// <summary>
/// Port for the classification + metadata prompt templates (spec §13.1/§13.2). The templates
/// live as editable resources in <c>Infrastructure/Llm/Prompts/</c> (NOT inline string literals
/// in the orchestrator), keeping the spec's "prompt library" an editable artifact. The contract
/// lives in Services so the orchestrator depends only on it; the resource-loading impl lives in
/// Infrastructure.
/// </summary>
public interface IPromptProvider
{
    /// <summary>
    /// Builds the classification prompt for the given (already truncated) document text. Fills the
    /// allowed-category list and the document text into the §13.1 template.
    /// </summary>
    string BuildClassificationPrompt(string documentText);

    /// <summary>
    /// Builds the metadata-extraction prompt, injecting the classification result (§13.2's
    /// <c>{{classification}}</c>) and the (truncated) document text.
    /// </summary>
    string BuildMetadataPrompt(string classification, string documentText);

    /// <summary>
    /// ADDITIVE (Phase-7 DA-049): builds the RAG answer prompt (§13.3), injecting the user's
    /// <c>{{question}}</c> and the assembled, source-labeled <c>{{retrievedChunks}}</c> context block.
    /// The mandatory grounding system instruction (§5.9) is exposed separately via
    /// <see cref="RagGroundingSystemPrompt"/> and passed as the LLM <c>System</c> message; this method
    /// builds the user-side prompt only. The RAG answer is PROSE (the orchestrator calls the LLM with
    /// <c>JsonMode=false</c>), unlike the JSON-mode classification/metadata prompts.
    /// </summary>
    string BuildRagPrompt(string question, string contextBlock);

    /// <summary>
    /// ADDITIVE (Phase-7 DA-049): the MANDATORY grounding system instruction (spec §5.9), used
    /// VERBATIM as the LLM <c>System</c> message on every RAG ask. Also the canonical source of the
    /// not-found phrase the orchestrator returns (short-circuit) and detects (post-LLM).
    /// </summary>
    string RagGroundingSystemPrompt { get; }

    /// <summary>
    /// ADDITIVE (Phase-7 DA-049): the EXACT canned not-found answer (spec §5.9 / §13.3) — returned by
    /// the short-circuit and matched (case-insensitive) against the model's output to flag
    /// <c>answerFound=false</c>.
    /// </summary>
    string RagNotFoundAnswer { get; }

    /// <summary>
    /// ADDITIVE (Phase-8 DA-054): builds the workflow-recommendation prompt (spec §5.11/§13.4),
    /// injecting the document's <c>{{classification}}</c> display string, its extracted
    /// <c>{{metadata}}</c> JSON, and a (truncated) <c>{{documentText}}</c> excerpt. The allowed
    /// priority list (<c>{{allowedPriorities}}</c>) is filled from the <c>WorkflowPriority</c> source
    /// of truth so the prompt and the coercer never drift. This is a JSON-mode call (the Phase-4
    /// classification posture, NOT the Phase-7 prose mode) — the orchestrator calls the LLM with
    /// <c>JsonMode=true</c> and validates/coerces the result.
    /// </summary>
    string BuildWorkflowRecommendationPrompt(string classification, string metadataJson, string documentText);
}
