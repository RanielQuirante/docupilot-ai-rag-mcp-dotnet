using System.Text.Json;
using DocuPilot.Models.Enums;
using DocuPilot.Services.Tools;
using FluentAssertions;

namespace DocuPilot.UnitTests.Workflow;

/// <summary>
/// Unit tests for <see cref="ToolInputSchema"/> — the lightweight in-house args validator (DA-054 /
/// ADR §4). Covers required-field presence, GUID parseability, enum membership (case-insensitive),
/// integer typing, optional-field tolerance, and the non-object-args rejection.
/// </summary>
public sealed class ToolInputSchemaTests
{
    private static JsonElement Args(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    private static readonly ToolInputSchema CreateTaskSchema = new(
        new ToolField("documentId", ToolFieldType.Guid, Required: true),
        new ToolField("taskType", ToolFieldType.String, Required: true),
        new ToolField("priority", ToolFieldType.Enum, Required: true, WorkflowPriorityNames.Names),
        new ToolField("reason", ToolFieldType.String, Required: false),
        new ToolField("limit", ToolFieldType.Integer, Required: false));

    [Fact]
    public void TryValidate_AllValid_Succeeds()
    {
        var ok = CreateTaskSchema.TryValidate(
            Args(new { documentId = Guid.CreateVersion7(), taskType = "LegalReview", priority = "high", reason = "x", limit = 5 }),
            out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryValidate_MissingRequiredField_Fails()
    {
        var ok = CreateTaskSchema.TryValidate(
            Args(new { documentId = Guid.CreateVersion7(), priority = "High" }),
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain("taskType");
    }

    [Fact]
    public void TryValidate_NonGuidDocumentId_Fails()
    {
        var ok = CreateTaskSchema.TryValidate(
            Args(new { documentId = "not-a-guid", taskType = "T", priority = "High" }),
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain("documentId");
    }

    [Fact]
    public void TryValidate_OffEnumPriority_Fails()
    {
        var ok = CreateTaskSchema.TryValidate(
            Args(new { documentId = Guid.CreateVersion7(), taskType = "T", priority = "URGENT" }),
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain("priority");
    }

    [Fact]
    public void TryValidate_OptionalFieldOmitted_Succeeds()
    {
        var ok = CreateTaskSchema.TryValidate(
            Args(new { documentId = Guid.CreateVersion7(), taskType = "T", priority = "Normal" }),
            out _);

        ok.Should().BeTrue();
    }

    [Fact]
    public void TryValidate_NonObjectArgs_Fails()
    {
        var ok = CreateTaskSchema.TryValidate(Args("a string"), out var error);

        ok.Should().BeFalse();
        error.Should().Contain("JSON object");
    }

    [Fact]
    public void ToJson_RendersFieldsAndAllowedValues()
    {
        var json = CreateTaskSchema.ToJson();

        json.Should().Contain("documentId");
        json.Should().Contain("priority");
        json.Should().Contain("Normal"); // enum allowed values present
    }
}
