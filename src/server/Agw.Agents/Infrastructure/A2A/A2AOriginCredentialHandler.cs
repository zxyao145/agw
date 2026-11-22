using System.Net;
using Agw.Agents.Application.A2A;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Infrastructure.A2A;

internal sealed class A2AOriginCredentialHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var binding = A2ARemoteHttpClient.CurrentRequest;
        var uri = request.RequestUri;
        var origin = uri is { IsAbsoluteUri: true } ? A2AEndpointRules.NormalizeOrigin(uri) : null;
        if (binding is null || uri is null || origin != binding.Target.Origin)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The A2A request must use its configured origin.");
        }

        ApplyCredentials(request, uri, binding.Target);
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            if (binding.OnResponse is { } onResponse)
            {
                await onResponse(response, cancellationToken);
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AgwException(
                    ErrorCodes.A2ARemoteAgentUnauthorized,
                    $"The remote A2A agent returned HTTP {(int)response.StatusCode}."
                );
            }
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void ApplyCredentials(HttpRequestMessage request, Uri uri, A2ARemoteHttpClient target)
    {
        // 在 transport 发送前附加 endpoint query，使 SDK 的 Activity 只记录路径。
        // Attach the endpoint query at transport time so SDK activities only see the path.
        var targetQuery = target.Target.Query;
        if (targetQuery.Length > 0 && targetQuery != uri.Query)
        {
            var query = uri.Query.Length > 0 ? $"{targetQuery}&{uri.Query[1..]}" : targetQuery;
            request.RequestUri = new UriBuilder(uri) { Query = query }.Uri;
        }

        foreach (var (name, value) in target.Headers)
        {
            if (request.Headers.TryAddWithoutValidation(name, value))
            {
                continue;
            }

            request.Content ??= new ByteArrayContent([]);
            if (!request.Content.Headers.TryAddWithoutValidation(name, value))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "An A2A credential header cannot be sent.");
            }
        }
    }
}
