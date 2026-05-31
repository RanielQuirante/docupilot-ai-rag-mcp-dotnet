using System.Text.Json;

namespace DocuPilot.Api.Controllers;

/// <summary>
/// Helper to build the <see cref="JsonElement"/> args the tool dispatcher validates + executes. The
/// API controllers adapt their bound request DTOs into the same JSON-args shape the dispatcher would
/// receive from any AI/agent caller, so the validation + audit path is identical regardless of caller.
/// </summary>
internal static class ToolJson
{
    public static JsonElement Args(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }
}
