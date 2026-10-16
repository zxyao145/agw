using System.Net.Http.Headers;
using System.Text.Json;
using Agw.Providers.Application;
using Agw.Providers.Contracts;
using Agw.Providers.Contracts.Manager;
using Agw.Shared.Exceptions;
using OllamaSharp;
using OllamaSharp.Models.Exceptions;

namespace Agw.Providers.Infrastructure;

public class ProviderModelDiscoveryService : IProviderModelDiscoveryService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ProviderModelDiscoveryService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ProviderModelDiscoveryResponse> DiscoverAsync(
        ProviderModelDiscoveryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        return request.ProviderType switch
        {
            ProviderType.OpenAIChatCompletions or ProviderType.OpenAIResponses => await DiscoverOpenAiModelsAsync(
                    request,
                    cancellationToken
                )
                .ConfigureAwait(false),
            ProviderType.OllamaNative => await DiscoverOllamaModelsAsync(request, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new AgwException(
                ErrorCodes.UnsupportedProviderType,
                $"Provider type '{request.ProviderType}' does not support model discovery."
            ),
        };
    }

    private async Task<ProviderModelDiscoveryResponse> DiscoverOpenAiModelsAsync(
        ProviderModelDiscoveryRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new AgwException(ErrorCodes.ProviderModelDiscoveryApiKeyRequired);
        }

        var modelsEndpoint = $"{request.Endpoint.TrimEnd('/')}/models";
        if (
            !Uri.TryCreate(modelsEndpoint, UriKind.Absolute, out var modelsUri)
            || (modelsUri.Scheme != Uri.UriSchemeHttp && modelsUri.Scheme != Uri.UriSchemeHttps)
        )
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
                    $"Provider model discovery returned HTTP {(int)response.StatusCode}."
                );
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
            )
            {
                throw new AgwException(ErrorCodes.ProviderModelDiscoveryFailed);
            }

            return CreateResponse(
                data.EnumerateArray()
                    .Select(item =>
                        item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String
                            ? id.GetString()
                            : null
                    )
            );
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

    private async Task<ProviderModelDiscoveryResponse> DiscoverOllamaModelsAsync(
        ProviderModelDiscoveryRequest request,
        CancellationToken cancellationToken
    )
    {
        // SDK 使用 api/tags 等相对路径，BaseAddress 以 / 结尾才能保留 Endpoint 的路径前缀。
        // The SDK sends relative paths such as api/tags; a trailing slash keeps the endpoint path prefix.
        if (
            !Uri.TryCreate($"{request.Endpoint.Trim().TrimEnd('/')}/", UriKind.Absolute, out var baseAddress)
            || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new AgwException(ErrorCodes.InvalidUrl, "Provider endpoint must be an absolute HTTP(S) URL.");
        }

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = baseAddress;
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                request.ApiKey.Trim()
            );
        }

        using var client = new OllamaApiClient(httpClient);
        try
        {
            var models = await client.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models == null)
            {
                throw new AgwException(ErrorCodes.ProviderModelDiscoveryFailed);
            }

            return CreateResponse(models.Select(model => model.Name));
        }
        catch (AgwException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OllamaException)
        {
            throw new AgwException(ErrorCodes.ProviderModelDiscoveryFailed);
        }
    }

    private static ProviderModelDiscoveryResponse CreateResponse(IEnumerable<string?> modelNames)
    {
        var names = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modelName in modelNames.Select(name => name?.Trim()))
        {
            if (!string.IsNullOrEmpty(modelName) && seenNames.Add(modelName))
            {
                names.Add(modelName);
            }
        }

        return new ProviderModelDiscoveryResponse(names);
    }
}
