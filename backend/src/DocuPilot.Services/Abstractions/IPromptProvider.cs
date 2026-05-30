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
}
