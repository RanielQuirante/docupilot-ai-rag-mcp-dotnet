namespace DocuPilot.Services.Documents;

/// <summary>
/// Thrown when no registered extractor can handle a document's format. A NON-transient
/// failure — the orchestrator must not retry it; the document goes straight to <c>Failed</c>
/// with a clear reason (ADR §3/§6).
/// </summary>
public sealed class UnsupportedFormatException : Exception
{
    public UnsupportedFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when extraction produced no usable text (empty / image-only / scanned PDF — ADR
/// §3, PM Q3). A NON-transient failure → the document goes to <c>Failed</c> (no retry).
/// </summary>
public sealed class EmptyExtractionException : Exception
{
    public EmptyExtractionException(string message) : base(message)
    {
    }
}
