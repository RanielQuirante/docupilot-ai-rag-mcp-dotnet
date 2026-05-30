using System.Security.Cryptography;
using System.Text;

namespace DocuPilot.Services.Documents;

/// <summary>
/// Derives a <b>deterministic</b> Qdrant point id (a valid UUID) from <c>(documentId, chunkIndex)</c>
/// (ADR §3). Same input always yields the same id, so re-embedding the same chunk overwrites the
/// same Qdrant point rather than duplicating — the vector-layer half of the belt-and-suspenders
/// idempotency (the SQL half is the composite <c>UNIQUE(DocumentId, ChunkIndex)</c>, DA-038).
/// <para>
/// Implemented as a UUIDv5-style namespaced hash: <c>SHA-1(namespace || "{documentId}:{chunkIndex}")</c>
/// truncated to 16 bytes with the version (5) + variant bits set, matching RFC 4122 v5 so the value
/// is a valid UUID Qdrant accepts. Deterministic and collision-resistant for our key space.
/// </para>
/// </summary>
public static class DeterministicPointId
{
    // A fixed application namespace (any stable GUID). Combined with the per-chunk name below.
    private static readonly byte[] NamespaceBytes =
        new Guid("3f1c0a2e-9b6d-4a7c-8e21-0c7a5d9f4b13").ToByteArray();

    /// <summary>Computes the deterministic point id for a chunk.</summary>
    public static Guid For(Guid documentId, int chunkIndex)
    {
        var name = $"{documentId:D}:{chunkIndex}";
        var nameBytes = Encoding.UTF8.GetBytes(name);

        Span<byte> input = stackalloc byte[NamespaceBytes.Length + nameBytes.Length];
        NamespaceBytes.CopyTo(input);
        nameBytes.CopyTo(input[NamespaceBytes.Length..]);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);

        // Set version (5) in the high nibble of byte 6 and the RFC 4122 variant in byte 8.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
