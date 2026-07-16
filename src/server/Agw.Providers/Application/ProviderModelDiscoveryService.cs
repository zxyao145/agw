using System.Net.Http.Headers;
using System.Text.Json;

using Agw.Providers.Contracts.Manager;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;

namespace Agw.Providers.Application;

public class ProviderModelDiscoveryService : IProviderModelDiscoveryService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ProviderModelDiscoveryService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ProviderModelDiscoveryResponse> DiscoverAsync(
        ProviderModelDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ProviderType is not ProviderType.OpenAIChatCompletions and not ProviderType.OpenAIResponses)
        {
            throw new AgwException(
                ErrorCodes.UnsupportedProviderType,
                $"Provider type '{request.ProviderType}' does not support model discovery.");
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new AgwException(ErrorCodes.ProviderModelDiscoveryApiKeyRequired);
        }

        var modelsEndpoint = $"{request.Endpoint.TrimEnd('/')}/models";
        if (!Uri.TryCreate(modelsEndpoint, UriKind.Absolute, out var modelsUri) ||
            (modelsUri.Scheme != Uri.UriSchemeHttp && modelsUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AgwException(ErrorCodes.InvalidUrl, "Provider endpoint must be an absolute HTTP(S) URL.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, modelsUri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());

        try
        {
            using var response = await _httpClientFactory
                .CreateClient()
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AgwException(
                    ErrorCodes.ProviderModelDiscoveryFailed,
                    $"Provider model discovery returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new AgwException(ErrorCodes.ProviderModelDiscoveryFailed);
            }

            var names = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var modelName = id.GetString()?.Trim();
                if (!string.IsNullOrEmpty(modelName) && seenNames.Add(modelName))
                {
                    names.Add(modelName);
                }
            }

            return new ProviderModelDiscoveryResponse(names);
        }
        catch (AgwException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new AgwException(ErrorCodes.ProviderModelDiscoveryFailed);
        }
    }
}
