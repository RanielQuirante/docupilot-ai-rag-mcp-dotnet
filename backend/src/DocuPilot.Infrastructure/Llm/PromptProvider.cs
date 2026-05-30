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

    private static readonly string AllowedCategoriesList =
        string.Join("\n", DocumentCategoryNames.DisplayNames.Select(name => $"- {name}"));

    public string BuildClassificationPrompt(string documentText) =>
        ClassificationTemplate
            .Replace("{{allowedCategories}}", AllowedCategoriesList)
            .Replace("{{documentText}}", documentText);

    public string BuildMetadataPrompt(string classification, string documentText) =>
        MetadataTemplate
            .Replace("{{classification}}", classification)
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
