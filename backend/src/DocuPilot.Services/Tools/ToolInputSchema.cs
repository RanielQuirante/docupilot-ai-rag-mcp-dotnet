using System.Text.Json;

namespace DocuPilot.Services.Tools;

/// <summary>The supported argument types in a tool's input schema (the lightweight in-house validator, ADR §4).</summary>
public enum ToolFieldType
{
    /// <summary>An arbitrary string.</summary>
    String,

    /// <summary>A string that must parse as a <see cref="System.Guid"/>.</summary>
    Guid,

    /// <summary>An integer (JSON number with no fractional part, or a numeric string).</summary>
    Integer,

    /// <summary>A string constrained to a fixed set of allowed values (case-insensitive).</summary>
    Enum,
}

/// <summary>A single field in a tool's input schema.</summary>
/// <param name="Name">The JSON property name.</param>
/// <param name="Type">The field type (drives validation).</param>
/// <param name="Required">Whether the field must be present and non-null/non-empty.</param>
/// <param name="AllowedValues">For <see cref="ToolFieldType.Enum"/>, the allowed values (case-insensitive); otherwise empty.</param>
public sealed record ToolField(string Name, ToolFieldType Type, bool Required, IReadOnlyList<string>? AllowedValues = null);

/// <summary>
/// A small, dependency-free JSON-schema-ish descriptor for a tool's arguments (spec §5.12
/// <c>inputSchema</c>). We deliberately do NOT pull in a full JSON-Schema library (over-engineering
/// for a handful of tools — ADR §4): this validates required-field presence + type + enum membership +
/// GUID parseability, which is exactly what the dispatcher needs to reject bad args before the handler
/// runs, and is fully unit-testable.
/// </summary>
public sealed class ToolInputSchema
{
    public ToolInputSchema(params ToolField[] fields)
    {
        Fields = fields;
    }

    public IReadOnlyList<ToolField> Fields { get; }

    /// <summary>
    /// Validates <paramref name="args"/> against the schema. Returns <c>true</c> on success; on
    /// failure returns <c>false</c> and an <paramref name="error"/> message (used by the dispatcher's
    /// <see cref="ToolResult.Rejected"/> + <c>ToolFailed</c> audit). A missing optional field is OK;
    /// a present-but-wrong-typed field (even optional) is an error.
    /// </summary>
    public bool TryValidate(JsonElement args, out string error)
    {
        error = string.Empty;

        if (args.ValueKind != JsonValueKind.Object)
        {
            error = "Arguments must be a JSON object.";
            return false;
        }

        foreach (var field in Fields)
        {
            var present = args.TryGetProperty(field.Name, out var value)
                          && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

            if (!present)
            {
                if (field.Required)
                {
                    error = $"'{field.Name}' is required.";
                    return false;
                }

                continue;
            }

            if (!ValidateField(field, value, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateField(ToolField field, JsonElement value, out string error)
    {
        error = string.Empty;
        switch (field.Type)
        {
            case ToolFieldType.String:
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    error = $"'{field.Name}' must be a non-empty string.";
                    return false;
                }

                return true;

            case ToolFieldType.Guid:
                if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out _))
                {
                    error = $"'{field.Name}' must be a GUID string.";
                    return false;
                }

                return true;

            case ToolFieldType.Integer:
                var isInt = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
                var isIntString = value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out _);
                if (!isInt && !isIntString)
                {
                    error = $"'{field.Name}' must be an integer.";
                    return false;
                }

                return true;

            case ToolFieldType.Enum:
                var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (raw is null
                    || field.AllowedValues is null
                    || !field.AllowedValues.Any(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase)))
                {
                    var allowed = field.AllowedValues is null ? string.Empty : string.Join(", ", field.AllowedValues);
                    error = $"'{field.Name}' must be one of: {allowed}.";
                    return false;
                }

                return true;

            default:
                error = $"'{field.Name}' has an unsupported field type.";
                return false;
        }
    }

    /// <summary>Renders the schema as a compact JSON string for introspection (<c>GET /api/tools</c>).</summary>
    public string ToJson()
    {
        var properties = Fields.ToDictionary(
            f => f.Name,
            f => new
            {
                type = f.Type.ToString().ToLowerInvariant(),
                required = f.Required,
                allowed = f.AllowedValues,
            });

        return JsonSerializer.Serialize(new { type = "object", properties });
    }
}
