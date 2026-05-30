using DocuPilot.Services.Abstractions;
using DocuPilot.Services.Documents;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DocuPilot.Infrastructure.Vector;

/// <summary>
/// <see cref="IVectorStore"/> implementation over the official <c>Qdrant.Client</c> (gRPC, ADR §3).
/// One collection (<c>document_chunks</c>, Cosine) holds all documents' chunks; per-document scoping
/// is a payload filter on <c>documentId</c>. Point ids are the deterministic ids computed by the
/// orchestrator (<see cref="DeterministicPointId"/>) so upsert is idempotent. The full chunk text is
/// NOT stored — only the payload (<c>documentId</c> [indexed], <c>chunkId</c>, <c>chunkIndex</c>,
/// <c>snippet</c> ~200 chars).
/// <para>
/// All RPC/connection faults are mapped to <see cref="VectorStoreUnavailableException"/> (the
/// orchestrator treats them as transient, ADR §6). The single exception is a dimension mismatch in
/// <see cref="EnsureCollectionAsync"/>, which fails loud (<see cref="InvalidOperationException"/>).
/// </para>
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private const string DocumentIdPayloadKey = "documentId";
    private const string ChunkIdPayloadKey = "chunkId";
    private const string ChunkIndexPayloadKey = "chunkIndex";
    private const string SnippetPayloadKey = "snippet";

    private readonly QdrantClient _client;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(QdrantClient client, IOptions<QdrantOptions> options, ILogger<QdrantVectorStore> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureCollectionAsync(int dimensions, CancellationToken ct)
    {
        try
        {
            var exists = await _client.CollectionExistsAsync(_options.CollectionName, ct);
            if (exists)
            {
                await ValidateDimensionAsync(dimensions, ct);
                return;
            }

            _logger.LogInformation(
                "Creating Qdrant collection '{Collection}' (size {Dimensions}, Cosine).",
                _options.CollectionName, dimensions);

            await _client.CreateCollectionAsync(
                _options.CollectionName,
                new VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine },
                cancellationToken: ct);

            // Index the documentId payload field so per-document filter deletes/scopes are fast.
            await _client.CreatePayloadIndexAsync(
                _options.CollectionName,
                DocumentIdPayloadKey,
                PayloadSchemaType.Keyword,
                cancellationToken: ct);
        }
        catch (InvalidOperationException)
        {
            // Dimension mismatch — fail loud (do NOT wrap as transient).
            throw;
        }
        catch (RpcException ex)
        {
            throw new VectorStoreUnavailableException(
                $"Qdrant unreachable while ensuring collection '{_options.CollectionName}' ({ex.Status.Detail}).", ex);
        }
    }

    private async Task ValidateDimensionAsync(int dimensions, CancellationToken ct)
    {
        var info = await _client.GetCollectionInfoAsync(_options.CollectionName, ct);
        var configured = info?.Config?.Params?.VectorsConfig?.Params?.Size;

        // Only validate the single (unnamed) vector configuration shape we create.
        if (configured is { } size && size != (ulong)dimensions)
        {
            throw new InvalidOperationException(
                $"Qdrant collection '{_options.CollectionName}' has vector size {size} but the embedding model " +
                $"reports {dimensions} dimensions. The collection must be recreated at the new dimension " +
                $"(swapping the embedding model requires a fresh Qdrant volume) — refusing to proceed (ADR §2/§3).");
        }
    }

    public async Task UpsertChunksAsync(IReadOnlyList<ChunkVector> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        var points = new List<PointStruct>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = chunk.PointId.ToString("D") },
                Vectors = chunk.Vector,
            };
            point.Payload[DocumentIdPayloadKey] = chunk.DocumentId.ToString("D");
            point.Payload[ChunkIdPayloadKey] = chunk.ChunkId.ToString("D");
            point.Payload[ChunkIndexPayloadKey] = chunk.ChunkIndex;
            point.Payload[SnippetPayloadKey] = chunk.Snippet ?? string.Empty;
            points.Add(point);
        }

        try
        {
            await _client.UpsertAsync(_options.CollectionName, points, cancellationToken: ct);
        }
        catch (RpcException ex)
        {
            throw new VectorStoreUnavailableException(
                $"Qdrant unreachable while upserting {chunks.Count} points ({ex.Status.Detail}).", ex);
        }
    }

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = DocumentIdPayloadKey,
                        Match = new Match { Keyword = documentId.ToString("D") },
                    },
                },
            },
        };

        try
        {
            await _client.DeleteAsync(_options.CollectionName, filter, cancellationToken: ct);
        }
        catch (RpcException ex)
        {
            throw new VectorStoreUnavailableException(
                $"Qdrant unreachable while deleting points for document {documentId} ({ex.Status.Detail}).", ex);
        }
    }

    public async Task<IReadOnlyList<ChunkHit>> SearchAsync(float[] query, int limit, Guid? documentId, CancellationToken ct)
    {
        Filter? filter = null;
        if (documentId is { } id)
        {
            filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = DocumentIdPayloadKey,
                            Match = new Match { Keyword = id.ToString("D") },
                        },
                    },
                },
            };
        }

        try
        {
            var results = await _client.SearchAsync(
                _options.CollectionName,
                query,
                filter: filter,
                limit: (ulong)Math.Max(1, limit),
                payloadSelector: true,
                cancellationToken: ct);

            var hits = new List<ChunkHit>(results.Count);
            foreach (var scored in results)
            {
                hits.Add(MapHit(scored));
            }

            return hits;
        }
        catch (RpcException ex)
        {
            throw new VectorStoreUnavailableException(
                $"Qdrant unreachable while searching collection '{_options.CollectionName}' ({ex.Status.Detail}).", ex);
        }
    }

    private static ChunkHit MapHit(ScoredPoint scored)
    {
        var payload = scored.Payload;

        var pointId = scored.Id?.Uuid is { Length: > 0 } uuid && Guid.TryParse(uuid, out var pid)
            ? pid
            : Guid.Empty;

        var documentId = payload.TryGetValue(DocumentIdPayloadKey, out var docVal)
            && Guid.TryParse(docVal.StringValue, out var did) ? did : Guid.Empty;

        var chunkId = payload.TryGetValue(ChunkIdPayloadKey, out var chunkVal)
            && Guid.TryParse(chunkVal.StringValue, out var cid) ? cid : Guid.Empty;

        var chunkIndex = payload.TryGetValue(ChunkIndexPayloadKey, out var idxVal)
            ? (int)idxVal.IntegerValue : 0;

        var snippet = payload.TryGetValue(SnippetPayloadKey, out var snipVal)
            ? snipVal.StringValue : null;

        return new ChunkHit(pointId, documentId, chunkId, chunkIndex, scored.Score, snippet);
    }
}
