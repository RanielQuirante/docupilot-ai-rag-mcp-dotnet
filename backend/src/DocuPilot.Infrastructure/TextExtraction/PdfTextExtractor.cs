using System.Text;
using DocuPilot.Services.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocuPilot.Infrastructure.TextExtraction;

/// <summary>
/// Extracts text from <c>.pdf</c> / <c>application/pdf</c> files using UglyToad.PdfPig (MIT,
/// pure-managed, no native deps — runs in the Linux container). Selectable-text PDFs only
/// (PM Q3): a scanned/image-only PDF yields little/no text → the orchestrator treats the
/// empty result as a clean <c>Failed</c> with a documented reason (OCR is out of POC scope).
/// </summary>
public sealed class PdfTextExtractor : ITextExtractor
{
    public bool CanHandle(string contentType, string fileName) =>
        PlainTextExtractor.HasExtension(fileName, ".pdf") ||
        contentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(Stream content, string contentType, string fileName, CancellationToken ct)
    {
        // PdfPig is synchronous and needs a seekable stream; buffer into memory (files are
        // already ≤ 25 MB at upload). Run off the request/poll thread.
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        return await Task.Run(() =>
        {
            var sb = new StringBuilder();
            using var pdf = PdfDocument.Open(buffer);
            foreach (Page page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                sb.AppendLine(page.Text);
            }

            return sb.ToString();
        }, ct);
    }
}
