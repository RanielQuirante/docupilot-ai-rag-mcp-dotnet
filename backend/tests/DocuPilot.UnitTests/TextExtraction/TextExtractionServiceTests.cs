using System.Text;
using DocuPilot.Infrastructure.TextExtraction;
using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using FluentAssertions;

namespace DocuPilot.UnitTests.TextExtraction;

/// <summary>
/// Unit tests for the extractor resolver/dispatch (<see cref="TextExtractionService"/>) and the
/// per-format extractors' <c>CanHandle</c> routing. Covers happy-path dispatch by extension and
/// content-type, and the unsupported-format → <see cref="UnsupportedFormatException"/> path.
/// </summary>
public sealed class TextExtractionServiceTests
{
    private static TextExtractionService CreateSut() => new(
    [
        new PlainTextExtractor(),
        new PdfTextExtractor(),
        new DocxTextExtractor(),
    ]);

    private static Stream Text(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task ExtractAsync_TxtFile_DispatchesToPlainTextExtractor()
    {
        var sut = CreateSut();

        var result = await sut.ExtractAsync(Text("hello world"), "text/plain", "note.txt", CancellationToken.None);

        result.Should().Be("hello world");
    }

    [Fact]
    public async Task ExtractAsync_TxtByContentTypeOnly_StillDispatches()
    {
        var sut = CreateSut();

        // No .txt extension but a text/plain content type — resolver still routes to plain text.
        var result = await sut.ExtractAsync(Text("body"), "text/plain; charset=utf-8", "blob", CancellationToken.None);

        result.Should().Be("body");
    }

    [Fact]
    public async Task ExtractAsync_UnsupportedFormat_ThrowsUnsupportedFormatException()
    {
        var sut = CreateSut();

        var act = async () => await sut.ExtractAsync(Text("x"), "application/octet-stream", "evil.exe", CancellationToken.None);

        await act.Should().ThrowAsync<UnsupportedFormatException>();
    }

    [Theory]
    [InlineData("note.txt", "text/plain", typeof(PlainTextExtractor))]
    [InlineData("report.pdf", "application/pdf", typeof(PdfTextExtractor))]
    [InlineData("memo.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", typeof(DocxTextExtractor))]
    public void CanHandle_RoutesToExpectedExtractor(string fileName, string contentType, Type expected)
    {
        var extractors = new ITextExtractor[]
        {
            new PlainTextExtractor(),
            new PdfTextExtractor(),
            new DocxTextExtractor(),
        };

        var match = extractors.FirstOrDefault(e => e.CanHandle(contentType, fileName));

        match.Should().NotBeNull();
        match!.GetType().Should().Be(expected);
    }
}
