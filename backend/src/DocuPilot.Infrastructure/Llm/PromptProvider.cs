using System.Reflection;
using DocuPilot.Models.Enums;
using DocuPilot.Services.Abstractions;

namespace DocuPilot.Infrastructure.Llm;

/// <summary>
/// Loads the classification + metadata prompt templates from the embedded resources in
/// <c>Llm/Prompts/</c> (spec §13.1/§13.2) and fills their placeholders. The templates are
/// editable <c>.txt</c> files (the spec's "prompt library"), embedded so they ship with the
/// assembly. The allowed-category list comes from the single <see cref="DocumentCategoryNames"/>
/// source of truth so the prompt and the validator never drift.
/// </summary>
public sealed class PromptProvider : IPromptProvider
{
    private static readonly string ClassificationTemplate =
        LoadResource("DocuPilot.Infrastructure.Llm.Prompts.ClassificationPrompt.txt");

    private static readonly string MetadataTemplate =
        LoadResource("DocuPilot.Infrastructure.Llm.Prompts.MetadataPrompt.txt");

    private static readonly string RagAnswerTemplate =
        LoadResource("DocuPilot.Infrastructure.Llm.Prompts.RagAnswerPrompt.txt");

    private static readonly string WorkflowRecommendationTemplate =
        LoadResource("DocuPilot.Infrastructure.Llm.Prompts.WorkflowRecommendationPrompt.txt");

    private static readonly string AllowedCategoriesList =
        string.Join("\n", DocumentCategoryNames.DisplayNames.Select(name => $"- {name}"));

    private static readonly string AllowedPrioritiesList =
        string.Join(", ", WorkflowPriorityNames.Names);

    // The MANDATORY grounding system instruction (spec §5.9), used VERBATIM as the LLM System message
    // on every RAG ask. NOT loaded from a resource — kept inline as the authoritative grounding
    // contract so it cannot be edited away by accident (the answer's correctness depends on it).
    private const string RagGroundingSystem =
        "Answer only using the provided document context. "
        + "If the answer is not found in the context, say: "
        + "\"I could not find enough information in the uploaded documents.\"";

    // The EXACT canned not-found phrase (spec §5.9 / §13.3) — returned by the short-circuit and
    // detected (case-insensitive) in the model output.
    private const string RagNotFound = "I could not find enough information in the uploaded documents.";

    public string BuildClassificationPrompt(string documentText) =>
        ClassificationTemplate
            .Replace("{{allowedCategories}}", AllowedCategoriesList)
            .Replace("{{documentText}}", documentText);

    public string BuildMetadataPrompt(string classification, string documentText) =>
        MetadataTemplate
            .Replace("{{classification}}", classification)
            .Replace("{{documentText}}", documentText);

    public string BuildRagPrompt(string question, string contextBlock) =>
        RagAnswerTemplate
            .Replace("{{question}}", question)
            .Replace("{{retrievedChunks}}", contextBlock);

    public string RagGroundingSystemPrompt => RagGroundingSystem;

    public string RagNotFoundAnswer => RagNotFound;

    public string BuildWorkflowRecommendationPrompt(string classification, string metadataJson, string documentText) =>
        WorkflowRecommendationTemplate
            .Replace("{{allowedPriorities}}", AllowedPrioritiesList)
            .Replace("{{classification}}", classification)
            .Replace("{{metadata}}", metadataJson)
            .Replace("{{documentText}}", documentText);

    private static string LoadResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded prompt resource '{resourceName}' was not found. Ensure the .txt is an EmbeddedResource.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
