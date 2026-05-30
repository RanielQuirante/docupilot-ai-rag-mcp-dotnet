using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocuPilot.Services.Abstractions;

namespace DocuPilot.Infrastructure.TextExtraction;

/// <summary>
/// Extracts text from <c>.docx</c> files using DocumentFormat.OpenXml (Microsoft, MIT,
/// pure-managed). Reads <c>word/document.xml</c> and concatenates paragraph text. Legacy
/// binary <c>.doc</c> is NOT supported (PM Q2 dropped it from the allow-list).
/// </summary>
public sealed class DocxTextExtractor : ITextExtractor
{
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public bool CanHandle(string contentType, string fileName) =>
        PlainTextExtractor.HasExtension(fileName, ".docx") ||
        contentType.StartsWith(DocxContentType, StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(Stream content, string contentType, string fileName, CancellationToken ct)
    {
        // OpenXml needs a seekable stream; buffer into memory (files are ≤ 25 MB at upload).
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        return await Task.Run(() =>
        {
            using var doc = WordprocessingDocument.Open(buffer, isEditable: false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                ct.ThrowIfCancellationRequested();

                // Concatenate the text runs of each paragraph, one paragraph per line.
                foreach (var text in paragraph.Descendants<Text>())
                {
                    sb.Append(text.Text);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }, ct);
    }
}
