using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Agw.Agents.Application.Acp;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Infrastructure.Acp;

public sealed class AcpRegistryClient : IAcpRegistryClient
{
    private readonly Lock _gate = new();
    private readonly AcpDownloadClient _downloads;
    private readonly AcpAgentOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AcpRegistryClient> _logger;
    private Task<AcpRegistrySnapshot>? _pending;
    private AcpRegistrySnapshot? _snapshot;
    private EntityTagHeaderValue? _etag;

    public AcpRegistryClient(
        AcpDownloadClient downloads,
        IOptions<AcpAgentOptions> options,
        TimeProvider clock,
        ILogger<AcpRegistryClient> logger
    )
    {
        _downloads = downloads;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public Task<AcpRegistrySnapshot> GetAsync(bool refresh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_pending is { IsCompleted: false })
                return _pending.WaitAsync(ct);
            if (
                !refresh
                && _snapshot is { } cached
                && _clock.GetUtcNow() - cached.FetchedAt < _options.RegistryRefreshInterval
            )
                return Task.FromResult(cached);
            _pending = ReadAsync(true, _snapshot, _etag, CancellationToken.None);
            return _pending.WaitAsync(ct);
        }
    }

    public Task<AcpRegistrySnapshot> FetchAsync(CancellationToken ct) => ReadAsync(false, null, null, ct);

    private async Task<AcpRegistrySnapshot> ReadAsync(
        bool cache,
        AcpRegistrySnapshot? previous,
        EntityTagHeaderValue? etag,
        CancellationToken ct
    )
    {
        using var timeout = new CancellationTokenSource(_options.RegistryFetchTimeout, _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var start = _clock.GetTimestamp();
        var outcome = "failed";
        int? status = null;
        try
        {
            using var request = CreateRequest(etag);
            using var response = await _downloads.SendAsync(request, deadline.Token);
            status = (int)response.StatusCode;
            var unchanged = response.StatusCode == HttpStatusCode.NotModified && previous is not null;
            var document = unchanged ? previous!.Document : await ReadDocumentAsync(response, deadline.Token);
            var snapshot = new AcpRegistrySnapshot(document, _clock.GetUtcNow(), _options.RegistryUrl);
            if (cache)
            {
                StoreSnapshot(snapshot, response.Headers.ETag ?? (unchanged ? etag : null));
            }
            outcome = unchanged ? "not_modified" : "fetched";
            return snapshot;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && IsFetchFailure(error))
        {
            if (previous is null)
            {
                _logger.LogWarning(error, "Failed to read ACP registry {RegistryUrl}", _options.RegistryUrl);
                throw new AgwException(
                    ErrorCodes.AcpRegistryUnavailable,
                    $"ACP registry request failed ({status?.ToString() ?? error.GetType().Name}): {error.Message}",
                    error
                );
            }
            outcome = "cached";
            _logger.LogWarning(error, "Using cached ACP registry from {RegistryUrl}", previous.SourceUrl);
            return previous;
        }
        finally
        {
            RecordFetch(outcome, status, start);
        }
    }

    private static bool IsFetchFailure(Exception error) =>
        error
            is HttpRequestException
                or IOException
                or InvalidDataException
                or JsonException
                or OperationCanceledException
                or AgwException
                or UriFormatException;

    private void RecordFetch(string outcome, int? status, long start)
    {
        AcpClientDiagnostics.RegistryFetches.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        _logger.LogInformation(
            "ACP registry fetch {Outcome}, HTTP {HttpStatus}, elapsed {ElapsedMs} ms",
            outcome,
            status,
            _clock.GetElapsedTime(start).TotalMilliseconds
        );
    }

    private HttpRequestMessage CreateRequest(EntityTagHeaderValue? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _options.RegistryUrl);
        if (etag is not null)
            request.Headers.IfNoneMatch.Add(etag);
        return request;
    }

    private void StoreSnapshot(AcpRegistrySnapshot snapshot, EntityTagHeaderValue? etag)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
            _etag = etag;
        }
    }

    private async Task<AcpRegistryDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        using var body = new MemoryStream();
        await AcpDownloadClient.CopyLimitedAsync(response.Content, body, _options.RegistryMaxBytes, null, ct);
        body.Position = 0;
        var document = await JsonSerializer.DeserializeAsync<AcpRegistryDocument>(body, JsonSerializerOptions.Web, ct);
        if (
            document?.Version is null
            || document.Agents is null
            || document.Agents.Any(agent =>
                agent?.Id is null
                || agent.Name is null
                || agent.Version is null
                || agent.Description is null
                || agent.Distribution is null
            )
        )
            throw new AgwException(ErrorCodes.AcpRegistryUnavailable, "Missing required ACP registry fields.");
        return document;
    }
}
