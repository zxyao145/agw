using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Bens.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Agw.Extensions.Infrastructure.Runtime;

public sealed class ExtensionEndpointDataSource : EndpointDataSource, IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _changed = new();
    private IReadOnlyList<Endpoint> _endpoints = [];
    private string _signature = "";

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            lock (_gate)
                return _endpoints;
        }
    }

    public override IChangeToken GetChangeToken()
    {
        lock (_gate)
            return new CancellationChangeToken(_changed.Token);
    }

    public void Publish(Func<IReadOnlyList<ExtensionGenerationSnapshot>> getSnapshots)
    {
        CancellationTokenSource changed;
        lock (_gate)
        {
            var active = getSnapshots()
                .Where(snapshot =>
                    snapshot.State
                        is ExtensionGenerationState.Ready
                            or ExtensionGenerationState.Active
                            or ExtensionGenerationState.Draining
                )
                .OrderBy(snapshot => snapshot.GenerationId.ExtensionId)
                .ToArray();
            var signature = string.Join(';', active.Select(snapshot => snapshot.GenerationId));
            if (_signature == signature)
                return;
            var endpoints = new List<Endpoint>();
            foreach (var generation in active)
            foreach (var endpoint in generation.Endpoints)
            {
                var extensionId = generation.GenerationId.ExtensionId;
                var endpointId = endpoint.Id;
                var route = new RouteEndpointBuilder(
                    context => InvokeAsync(context, extensionId, endpointId),
                    RoutePatternFactory.Parse($"/api/ext/{generation.Name}/{endpoint.RelativePath}"),
                    0
                );
                route.DisplayName = $"{generation.Name}: {endpoint.Id}";
                route.Metadata.Add(endpoint.IsCallback ? new AllowAnonymousAttribute() : new AuthorizeAttribute());
                route.Metadata.Add(new HttpMethodMetadata([endpoint.Method]));
                endpoints.Add(route.Build());
            }
            _endpoints = endpoints;
            _signature = signature;
            changed = _changed;
            _changed = new CancellationTokenSource();
        }
        changed.Cancel();
        changed.Dispose();
    }

    private static async Task InvokeAsync(HttpContext context, Guid extensionId, string endpointId)
    {
        var catalog = context.RequestServices.GetRequiredService<IExtensionGenerationCatalog>();
        await using var generation = await catalog.AcquireCurrentAsync(extensionId, context.RequestAborted);
        var descriptor =
            generation.Snapshot.Endpoints.SingleOrDefault(endpoint => endpoint.Id == endpointId)
            ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
        if (
            !descriptor.IsCallback
            && !descriptor.IsBrowserPage
            && context.Request.Headers["X-Agw-Extension-Id"] != extensionId.ToString()
        )
            throw new AgwException(ErrorCodes.ExtensionVersionMismatch);
        using var logScope = context
            .RequestServices.GetRequiredService<ILogger<ExtensionEndpointDataSource>>()
            .BeginScope(
                new Dictionary<string, object?>
                {
                    ["ServerId"] = await context
                        .RequestServices.GetRequiredService<IExtensionDeploymentIdentity>()
                        .GetServerIdAsync(context.RequestAborted),
                    ["HostId"] = context.RequestServices.GetRequiredService<IExtensionHostIdentity>().HostId,
                    ["ExtensionId"] = extensionId,
                    ["ExtensionName"] = generation.Snapshot.Name,
                    ["ReleaseHash"] = generation.Pin.ReleaseHash,
                    ["GenerationId"] = generation.Snapshot.GenerationId.GenerationId,
                }
            );
        JsonElement? body = null;
        byte[]? rawBody = null;
        if (context.Features.GetRequiredFeature<IHttpRequestBodyDetectionFeature>().CanHaveBody)
        {
            try
            {
                Stream requestBody = context.Request.Body;
                if (descriptor.IsCallback)
                {
                    using var buffer = new MemoryStream();
                    var chunk = new byte[8192];
                    int count;
                    while ((count = await requestBody.ReadAsync(chunk, context.RequestAborted)) != 0)
                    {
                        if (buffer.Length + count > 1024 * 1024)
                            throw new AgwException(ErrorCodes.ExtensionPayloadTooLarge);
                        buffer.Write(chunk, 0, count);
                    }
                    rawBody = buffer.ToArray();
                    requestBody = new MemoryStream(rawBody, writable: false);
                }
                using var document = await JsonDocument.ParseAsync(
                    requestBody,
                    cancellationToken: context.RequestAborted
                );
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Extension request body must be JSON.");
            }
        }
        var request = JsonSerializer.SerializeToElement(
            new
            {
                method = context.Request.Method,
                query = context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()),
                body,
                headers = context.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()),
                rawBody,
            }
        );
        var invoker = context.RequestServices.GetRequiredService<IExtensionInvoker>();
        await using var response = await invoker.InvokeEndpointAsync(
            generation,
            new ExtensionEndpointInvocation
            {
                EndpointId = endpointId,
                ReleaseHash = generation.Pin.ReleaseHash,
                Epoch = generation.Pin.Epoch,
                Request = request,
            },
            context.RequestAborted
        );
        ApiResult<JsonElement?> result = CreateResult(response.StatusCode, response.JsonBody);
        if (descriptor.IsCallback || descriptor.IsBrowserPage)
        {
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;
            if (response.BodyStream is { } callbackStream)
            {
                await foreach (var bytes in callbackStream.WithCancellation(context.RequestAborted))
                    await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
            }
            else if (response.JsonBody is { } callbackBody)
            {
                await context.Response.WriteAsync(callbackBody.GetRawText(), context.RequestAborted);
            }
            await context.Response.CompleteAsync();
            await response.CompleteAsync(context.RequestAborted);
            return;
        }
        if (response.BodyStream is { } stream)
        {
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;
            var json = response.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
            if (json)
                await context.Response.WriteAsync(
                    $"{{\"code\":{result.Code},\"title\":{JsonSerializer.Serialize(result.Title)},\"statusCode\":{response.StatusCode},\"data\":",
                    context.RequestAborted
                );
            await foreach (var bytes in stream.WithCancellation(context.RequestAborted))
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
            if (json)
                await context.Response.WriteAsync("}", context.RequestAborted);
        }
        else
        {
            await ((Microsoft.AspNetCore.Http.IResult)result).ExecuteAsync(context);
        }
        await context.Response.CompleteAsync();
        await response.CompleteAsync(context.RequestAborted);
    }

    private static ApiResult<JsonElement?> CreateResult(int statusCode, JsonElement? body)
    {
        if (statusCode is >= 200 and < 300)
        {
            return new ApiResult<JsonElement?>().WithCode(0).WithTitle("OK").WithStatusCode(statusCode).WithData(body);
        }

        string title = ReasonPhrases.GetReasonPhrase(statusCode);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = ErrorCodes.ExtensionUnavailable.Message;
        }

        return ApiResult.Fail<JsonElement?>(title, statusCode).WithStatusCode(statusCode).WithData(body);
    }

    public void Dispose()
    {
        lock (_gate)
            _changed.Dispose();
    }
}
