using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.Memory;
using Microsoft.Extensions.Logging;

namespace LifeOS.Infrastructure.Memory;

// AI-004: client of POST /v1/journal-memory/index-entry and /embed-query on the Python AI service (same
// service, base URL and key as the other capabilities: NutritionAi options). Sends journal text only:
// never a user id, entry id or any other LifeOS identifier. Every failure is a result, never an
// exception; the service's messages do not leave this class, and neither journal text, questions,
// chunks, vectors nor the key is ever logged (only status codes, counts and latency).
//
// The answer is deserialized as-is; JournalMemoryValidation decides whether it is usable.
internal sealed class JournalEmbeddingClient : IJournalEmbeddingService, IDisposable
{
    private const string IndexPath = "v1/journal-memory/index-entry";
    private const string QueryPath = "v1/journal-memory/embed-query";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient? _httpClient;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly ILogger<JournalEmbeddingClient> _logger;

    public JournalEmbeddingClient(HttpClient? httpClient, string? serviceKey, ILogger<JournalEmbeddingClient> logger)
    {
        if (httpClient is not null && string.IsNullOrWhiteSpace(serviceKey))
        {
            throw new ArgumentException("A configured AI service requires a service key.", nameof(serviceKey));
        }

        _httpClient = httpClient;
        _authorization = httpClient is null ? null : new AuthenticationHeaderValue("Bearer", serviceKey!.Trim());
        _logger = logger;
    }

    public async Task<JournalIndexingResult> IndexEntryAsync(string? title, string content, CancellationToken cancellationToken)
    {
        var (body, failure) = await PostAsync<IndexResponse>(IndexPath, new IndexRequest(title, content), "index", cancellationToken);

        if (failure is { } failed)
        {
            return JournalIndexingResult.Failed(failed);
        }

        if (body is not { OutputVersion: 1, ChunkingVersion: { } chunking, EmbeddingProvider: { } provider, EmbeddingModel: { } model, EmbeddingDimensions: { } dimensions, Chunks: { } chunks }
            || chunks.Any(chunk => chunk is not { Ordinal: not null, Text: not null, Embedding: not null }))
        {
            _logger.LogWarning("Journal memory AI service returned an incomplete index.");
            return JournalIndexingResult.Failed(JournalMemoryAiFailure.InvalidOutput);
        }

        _logger.LogInformation("Journal memory index answer: {ChunkCount} chunks, {Provider} {Model} ({Dimensions} dimensions), chunking {ChunkingVersion}.",
            chunks.Count, provider, model, dimensions, chunking);

        return JournalIndexingResult.Success(new JournalIndexedEntry(
            new JournalMemoryIndexIdentity(chunking, provider, model, dimensions),
            chunks.Select(chunk => new JournalIndexedChunk(chunk!.Ordinal!.Value, chunk.Text!, chunk.Embedding!)).ToList()));
    }

    public async Task<JournalQueryEmbeddingResult> EmbedQueryAsync(string question, CancellationToken cancellationToken)
    {
        var (body, failure) = await PostAsync<QueryResponse>(QueryPath, new QueryRequest(question), "query", cancellationToken);

        if (failure is { } failed)
        {
            return JournalQueryEmbeddingResult.Failed(failed);
        }

        if (body is not { OutputVersion: 1, Provider: { } provider, Model: { } model, Embedding: { } embedding })
        {
            _logger.LogWarning("Journal memory AI service returned an incomplete query embedding.");
            return JournalQueryEmbeddingResult.Failed(JournalMemoryAiFailure.InvalidOutput);
        }

        return JournalQueryEmbeddingResult.Success(new JournalQueryEmbedding(provider, model, embedding));
    }

    public void Dispose() => _httpClient?.Dispose();

    private async Task<(T? Body, JournalMemoryAiFailure? Failure)> PostAsync<T>(string path, object payload, string purpose, CancellationToken cancellationToken)
        where T : class
    {
        if (_httpClient is null)
        {
            return (null, JournalMemoryAiFailure.Unavailable);
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Authorization = _authorization;

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning("Journal memory AI service ({Purpose}) returned HTTP {StatusCode} after {ElapsedMs} ms.",
                    purpose, (int)response.StatusCode, Elapsed(started));

                // 502: the provider answered without valid embeddings. 422: the service rejected the
                // input (retrying the same text cannot help). Anything else (503, 500, 401 for a
                // rejected service key, ...) means the service cannot answer right now.
                return (null, response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.UnprocessableEntity
                    ? JournalMemoryAiFailure.InvalidOutput
                    : JournalMemoryAiFailure.Unavailable);
            }

            var body = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);

            _logger.LogInformation("Journal memory AI service ({Purpose}) answered in {ElapsedMs} ms.", purpose, Elapsed(started));

            return (body, null);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning("Journal memory AI service ({Purpose}) returned an unreadable answer.", purpose);
            return (null, JournalMemoryAiFailure.InvalidOutput);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("Journal memory AI service ({Purpose}) is unreachable or timed out ({ExceptionType}) after {ElapsedMs} ms.",
                purpose, exception.GetType().Name, Elapsed(started));
            return (null, JournalMemoryAiFailure.Unavailable);
        }
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    internal sealed record IndexRequest(string? Title, string Content);

    internal sealed record QueryRequest(string Question);

    private sealed record IndexResponse(
        int? OutputVersion,
        string? ChunkingVersion,
        string? EmbeddingProvider,
        string? EmbeddingModel,
        int? EmbeddingDimensions,
        List<ChunkPayload?>? Chunks);

    private sealed record ChunkPayload(int? Ordinal, string? Text, float[]? Embedding);

    private sealed record QueryResponse(int? OutputVersion, string? Provider, string? Model, int? Dimensions, float[]? Embedding);
}
