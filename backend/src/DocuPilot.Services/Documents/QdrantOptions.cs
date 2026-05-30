namespace DocuPilot.Services.Documents;

/// <summary>
/// Qdrant connection + collection bounds, bound from the <c>Qdrant</c> config section (env keys
/// <c>Qdrant__Host</c> / <c>Qdrant__GrpcPort</c> / <c>Qdrant__CollectionName</c> /
/// <c>Qdrant__UseTls</c>). Defaults per ADR §3: in-network service <c>qdrant</c> on gRPC port
/// <c>6334</c>, collection <c>document_chunks</c>, plaintext (in-network). Lives in Services so the
/// vector-store impl binds it. DevOps (DA-043) wires these on api + worker and documents them in
/// <c>.env.example</c>.
/// </summary>
public sealed class QdrantOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Qdrant";

    /// <summary>In-network Qdrant host (compose service name). Default <c>qdrant</c>.</summary>
    public string Host { get; set; } = "qdrant";

    /// <summary>Qdrant gRPC port (container-internal). Default <c>6334</c> (the host remap is irrelevant in-network).</summary>
    public int GrpcPort { get; set; } = 6334;

    /// <summary>Single shared collection holding all documents' chunks. Default <c>document_chunks</c>.</summary>
    public string CollectionName { get; set; } = "document_chunks";

    /// <summary>Whether to use TLS to reach Qdrant. Default <c>false</c> (in-network plaintext).</summary>
    public bool UseTls { get; set; }
}
