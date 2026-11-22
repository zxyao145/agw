using System.Text.Json;
using global::A2A;
using Agw.Agents.Application.A2A;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.ExternalAgents.A2A;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Agw.Agents.ExternalAgents.A2A.A2AClientDiagnostics;
using static Agw.Agents.ExternalAgents.A2A.A2ARequestErrors;

namespace Agw.Agents.Infrastructure.A2A;

internal sealed class A2AAgentCardClient : IA2AAgentCardClient
{
    private const int CardReadBufferSize = 8192;
    private static readonly string[] SupportedBindings = [ProtocolBindingNames.HttpJson, ProtocolBindingNames.JsonRpc];
    private readonly IA2ARemoteHttpClientFactory _httpClients;
    private readonly A2ARemoteAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<A2AAgentCardClient> _logger;

    public A2AAgentCardClient(
        IA2ARemoteHttpClientFactory httpClients,
        IOptions<A2ARemoteAgentOptions> options,
        TimeProvider timeProvider,
        ILogger<A2AAgentCardClient> logger
    )
    {
        _httpClients = httpClients;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<A2AResolvedCard> FetchAsync(
        Uri cardUrl,
        A2AEndpointCredentials credentials,
        CancellationToken cancellationToken
    )
    {
        var host = cardUrl.IdnHost;
        using var activity = StartCard(host);
        var started = _timeProvider.GetTimestamp();
        var outcome = CardOutcome.FetchFailed;
        try
        {
            var card = await FetchCoreAsync(cardUrl, credentials, cancellationToken);
            var candidates = (card.SupportedInterfaces ?? [])
                .OfType<AgentInterface>()
                .Select(candidate => (candidate.Url, candidate.ProtocolBinding));
            var selected = A2AEndpointRules.SelectInterface(candidates, SupportedBindings);
            var json = JsonSerializer.SerializeToElement(card, A2AJsonUtilities.DefaultOptions);
            outcome = CardOutcome.Ok;
            return new(json, selected, card.Capabilities?.Streaming == true);
        }
        catch (Exception exception)
            when (IsRequestException(exception)
                && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            )
        {
            var error = MapFetchFailure(exception);
            if (error.Code == ErrorCodes.A2AAgentCardInvalid.Code)
                outcome = CardOutcome.Invalid;
            throw error;
        }
        finally
        {
            RecordCard(activity, outcome);
            _logger.LogInformation(
                "A2A card fetch from {CardHost} finished with {Outcome} in {ElapsedMs} ms.",
                host,
                OutcomeName(outcome),
                _timeProvider.GetElapsedTime(started).TotalMilliseconds
            );
        }
    }

    private async Task<AgentCard> FetchCoreAsync(
        Uri cardUrl,
        A2AEndpointCredentials credentials,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CardFetchTimeout);
        using var client = _httpClients.Create(cardUrl, credentials);
        var resolverUrl = new Uri(cardUrl.GetLeftPart(UriPartial.Path));
        var cardPath = resolverUrl.AbsolutePath.EndsWith('/') ? "." : $"./{resolverUrl.Segments[^1]}";
        var resolver = new A2ACardResolver(resolverUrl, client.Client, cardPath);
        return await client.InvokeAsync(
            () => new ValueTask<AgentCard>(resolver.GetAgentCardAsync(timeout.Token)),
            ReadBoundedCardAsync
        );
    }

    private async ValueTask ReadBoundedCardAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new AgwException(
                ErrorCodes.A2AAgentCardFetchFailed,
                $"The A2A agent card request returned HTTP {(int)response.StatusCode}."
            );
        }

        var maxBytes = _options.CardMaxBytes;
        using var content = response.Content;
        if (content.Headers.ContentLength > maxBytes)
        {
            throw new AgwException(ErrorCodes.A2AAgentCardInvalid);
        }

        using var body = new MemoryStream();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[Math.Min(CardReadBufferSize, maxBytes)];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > maxBytes)
                throw new AgwException(ErrorCodes.A2AAgentCardInvalid);
            body.Write(buffer, 0, read);
        }
        response.Content = new ByteArrayContent(body.GetBuffer(), 0, (int)body.Length);
        response.Content.Headers.ContentType = content.Headers.ContentType;
    }

    private static AgwException MapFetchFailure(Exception exception)
    {
        var cause = SafeException(exception);
        if (PreserveKnownError(exception, cause) is { } known)
            return known;

        var http = FindHttpError(exception);
        if (http?.StatusCode is { } status)
        {
            return new AgwException(
                ErrorCodes.A2AAgentCardFetchFailed,
                $"The A2A agent card request returned HTTP {(int)status}.",
                cause
            );
        }
        if (exception is OperationCanceledException)
        {
            return new AgwException(ErrorCodes.A2AAgentCardFetchFailed, "The A2A agent card request timed out.", cause);
        }
        var code =
            http is not null || exception is IOException
                ? ErrorCodes.A2AAgentCardFetchFailed
                : ErrorCodes.A2AAgentCardInvalid;
        return new AgwException(code, code.Message, cause);
    }
}
