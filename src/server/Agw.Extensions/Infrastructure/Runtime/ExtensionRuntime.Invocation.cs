using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Shared.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Infrastructure.Runtime;

public sealed partial class ExtensionRuntime
{
    public async ValueTask<IExtensionInvocationResponse> InvokeEndpointAsync(
        IExtensionGenerationLease generationLease,
        ExtensionEndpointInvocation invocation,
        CancellationToken cancellationToken = default
    )
    {
        (GenerationLease lease, ExtensionGeneration generation) = GetUsableGenerationLease(generationLease);
        ValidateInvocationPin(lease.Pin, invocation.ReleaseHash, invocation.Epoch);
        RegisteredEndpoint endpoint =
            generation.Registration.Endpoints.FirstOrDefault(candidate =>
                candidate.Descriptor.Id == invocation.EndpointId
            ) ?? throw new AgwException(ErrorCodes.InvalidParam);
        ExtensionEndpointRequest request = DeserializeEndpointRequest(invocation.Request);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: true,
            backgroundWork: false,
            token => _availability.CanContinuePinnedWorkAsync(lease.Pin, token),
            cancellationToken
        );
        try
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            ExtensionEndpointResponse response = await endpoint.Handler(
                scope.Services,
                scope.Context,
                request,
                cancellationToken
            );
            ValidateResponse(response);
            return new ExtensionInvocationResponse(
                response,
                scope.Resources,
                generation.GenerationId,
                generation.Request.ReleaseHash,
                _logger
            );
        }
        catch (Exception exception)
        {
            await scope.DisposeAsync();
            RethrowSafeException(exception, cancellationToken);

            _logger.LogError(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} endpoint {EndpointId} failed: {Failure}",
                generation.GenerationId.ExtensionId,
                generation.Request.ReleaseHash,
                generation.GenerationId.GenerationId,
                endpoint.Descriptor.Id,
                exception.ToString()
            );
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    public async ValueTask<IExtensionInvocationResponse> InvokeToolAsync(
        IExtensionGenerationLease generationLease,
        ExtensionToolInvocation invocation,
        CancellationToken cancellationToken = default
    )
    {
        (GenerationLease lease, ExtensionGeneration generation) = GetUsableGenerationLease(generationLease);
        ValidateInvocationPin(lease.Pin, invocation.ReleaseHash, invocation.Epoch);
        RegisteredTool toolRegistration =
            generation.Registration.Tools.FirstOrDefault(candidate => candidate.Descriptor.Id == invocation.ToolId)
            ?? throw new AgwException(ErrorCodes.InvalidParam);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: true,
            backgroundWork: false,
            token => _availability.CanContinuePinnedWorkAsync(lease.Pin, token),
            cancellationToken
        );
        try
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            IExtensionTool? tool = (IExtensionTool)scope.Services.GetRequiredService(toolRegistration.ToolType);
            if (
                tool.Name != toolRegistration.Descriptor.Name
                || tool.RequiredPermission != toolRegistration.Descriptor.RequiredPermission
                || tool.AllowInPlanMode != toolRegistration.Descriptor.AllowInPlanMode
            )
            {
                throw new AgwException(
                    ErrorCodes.ExtensionPackageInvalid,
                    "Extension tool metadata changed after registration."
                );
            }

            AIFunction? function = tool.CreateFunction(generation.Registration.JsonOptions);
            if (function is null)
            {
                throw new AgwException(
                    ErrorCodes.ExtensionPackageInvalid,
                    "Extension tool must produce an invokable function."
                );
            }

            AIFunctionArguments arguments = CreateToolArguments(invocation.Arguments);
            object? result = await function.InvokeAsync(arguments, cancellationToken);
            JsonElement body = result is JsonElement jsonElement
                ? jsonElement.Clone()
                : JsonSerializer.SerializeToElement(
                    result,
                    result?.GetType() ?? typeof(object),
                    generation.Registration.JsonOptions
                );
            result = null;
            function = null;
            tool = null;
            return new ExtensionInvocationResponse(
                ExtensionEndpointResponse.FromJson(body),
                scope.Resources,
                generation.GenerationId,
                generation.Request.ReleaseHash,
                _logger
            );
        }
        catch (Exception exception)
        {
            await scope.DisposeAsync();
            RethrowSafeException(exception, cancellationToken);

            _logger.LogError(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} tool {ToolId} failed: {Failure}",
                generation.GenerationId.ExtensionId,
                generation.Request.ReleaseHash,
                generation.GenerationId.GenerationId,
                toolRegistration.Descriptor.Id,
                exception.ToString()
            );
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    public async ValueTask<IExtensionInvocationResponse> InvokeSkillContentAsync(
        IExtensionGenerationLease generationLease,
        ExtensionSkillInvocation invocation,
        CancellationToken cancellationToken = default
    )
    {
        (GenerationLease lease, ExtensionGeneration generation) = GetUsableGenerationLease(generationLease);
        ValidateInvocationPin(lease.Pin, invocation.ReleaseHash, invocation.Epoch);
        RegisteredSkill skill =
            generation.Registration.Skills.FirstOrDefault(candidate => candidate.Descriptor.Id == invocation.SkillId)
            ?? throw new AgwException(ErrorCodes.InvalidParam);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: true,
            backgroundWork: false,
            token => _availability.CanContinuePinnedWorkAsync(lease.Pin, token),
            cancellationToken
        );
        try
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            string content = await skill.ContentProvider(scope.Services, scope.Context, cancellationToken);
            JsonElement body = JsonSerializer.SerializeToElement(content);
            return new ExtensionInvocationResponse(
                ExtensionEndpointResponse.FromJson(body),
                scope.Resources,
                generation.GenerationId,
                generation.Request.ReleaseHash,
                _logger
            );
        }
        catch (Exception exception)
        {
            await scope.DisposeAsync();
            RethrowSafeException(exception, cancellationToken);

            _logger.LogError(
                "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} skill {SkillId} failed: {Failure}",
                generation.GenerationId.ExtensionId,
                generation.Request.ReleaseHash,
                generation.GenerationId.GenerationId,
                skill.Descriptor.Id,
                exception.ToString()
            );
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    private async ValueTask ExecuteBackgroundWorkAsync(
        ExtensionGenerationId generationId,
        Func<IExtensionContext, CancellationToken, ValueTask> work,
        CancellationToken cancellationToken
    )
    {
        ExtensionGeneration generation = GetGeneration(generationId);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: false,
            backgroundWork: true,
            token => _availability.CanAcceptNewWorkAsync(CreatePin(generation), token),
            cancellationToken
        );
        await using (scope)
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            await work(scope.Context, cancellationToken);
        }
    }

    private async ValueTask<TResult> ExecuteBackgroundWorkAsync<TResult>(
        ExtensionGenerationId generationId,
        Func<IExtensionContext, CancellationToken, ValueTask<TResult>> work,
        CancellationToken cancellationToken
    )
    {
        ExtensionGeneration generation = GetGeneration(generationId);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: false,
            backgroundWork: true,
            token => _availability.CanAcceptNewWorkAsync(CreatePin(generation), token),
            cancellationToken
        );
        await using (scope)
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            return await work(scope.Context, cancellationToken);
        }
    }

    private async ValueTask ExecuteBackgroundWorkAsync(
        ExtensionGenerationId generationId,
        string userId,
        Guid callerInvocationId,
        Func<IExtensionContext, CancellationToken, ValueTask> work,
        CancellationToken cancellationToken
    )
    {
        ValidateUserAndInvocation(userId, callerInvocationId);
        ExtensionGeneration generation = GetGeneration(generationId);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: false,
            backgroundWork: true,
            token => _availability.CanAcceptNewWorkAsync(CreatePin(generation), token),
            cancellationToken,
            userId,
            callerInvocationId
        );
        await using (scope)
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            await work(scope.Context, cancellationToken);
        }
    }

    private async ValueTask<TResult> ExecuteBackgroundWorkAsync<TResult>(
        ExtensionGenerationId generationId,
        string userId,
        Guid callerInvocationId,
        Func<IExtensionContext, CancellationToken, ValueTask<TResult>> work,
        CancellationToken cancellationToken
    )
    {
        ValidateUserAndInvocation(userId, callerInvocationId);
        ExtensionGeneration generation = GetGeneration(generationId);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: false,
            backgroundWork: true,
            token => _availability.CanAcceptNewWorkAsync(CreatePin(generation), token),
            cancellationToken,
            userId,
            callerInvocationId
        );
        await using (scope)
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            return await work(scope.Context, cancellationToken);
        }
    }

    private async ValueTask<InvocationScope> BeginInvocationAsync(
        ExtensionGeneration generation,
        bool allowDraining,
        bool backgroundWork,
        Func<CancellationToken, ValueTask<bool>> isAvailable,
        CancellationToken cancellationToken,
        string? userId = null,
        Guid? callerInvocationId = null
    )
    {
        if (!generation.TryStartInvocation(allowDraining, backgroundWork, out Guid invocationId))
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        KeyValuePair<string, object?>[] metricTags = CreateMetricTags(generation);
        ActiveCalls.Add(1, metricTags);
        AsyncServiceScope? hostScope = null;
        AsyncServiceScope? generationScope = null;
        try
        {
            if (!await isAvailable(cancellationToken))
            {
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }

            hostScope = _hostServices.CreateAsyncScope();
            generationScope =
                generation.Services?.CreateAsyncScope() ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
            ICurrentUser currentUser = hostScope.Value.ServiceProvider.GetRequiredService<ICurrentUser>();
            var context = new ExtensionContext(
                generation.Request,
                generation.GenerationId,
                generation.HostCapabilities,
                currentUser,
                hostScope.Value.ServiceProvider,
                generation.Registration.JsonOptions,
                generation.Logger
            );
            var resources = new ExtensionInvocationResources(
                generation,
                generationScope.Value,
                hostScope.Value,
                userId ?? currentUser.UserId,
                invocationId,
                callerInvocationId ?? invocationId,
                _callerContext,
                _logger,
                () => ActiveCalls.Add(-1, metricTags)
            );
            var scope = new InvocationScope(generationScope.Value.ServiceProvider, context, resources);
            hostScope = null;
            generationScope = null;
            return scope;
        }
        catch
        {
            try
            {
                if (generationScope is { } activeGenerationScope)
                {
                    await activeGenerationScope.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    if (hostScope is { } activeHostScope)
                    {
                        await activeHostScope.DisposeAsync();
                    }
                }
                finally
                {
                    generation.FinishInvocation(invocationId);
                    ActiveCalls.Add(-1, metricTags);
                }
            }

            throw;
        }
    }

    private (GenerationLease Lease, ExtensionGeneration Generation) GetUsableGenerationLease(
        IExtensionGenerationLease generationLease
    )
    {
        if (
            generationLease is not GenerationLease lease
            || !ReferenceEquals(lease.Owner, this)
            || !lease.TryGetGeneration(out ExtensionGeneration? generation)
            || generation is null
        )
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        return (lease, generation);
    }

    private static void ValidateInvocationPin(ExtensionExecutionPin pin, string releaseHash, long epoch)
    {
        if (pin.ReleaseHash != releaseHash || pin.Epoch != epoch)
        {
            throw new AgwException(ErrorCodes.ExtensionVersionMismatch);
        }
    }

    private static ExtensionEndpointRequest DeserializeEndpointRequest(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        try
        {
            return request.Deserialize<ExtensionEndpointRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new AgwException(ErrorCodes.InvalidParam);
        }
        catch (JsonException)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    private static AIFunctionArguments CreateToolArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        var result = new AIFunctionArguments();
        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static void ValidateResponse(ExtensionEndpointResponse response)
    {
        if (
            response.StatusCode is < 100 or > 599
            || string.IsNullOrWhiteSpace(response.ContentType)
            || (response.JsonBody is not null && response.BodyStream is not null)
        )
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    private sealed class InvocationScope : IAsyncDisposable
    {
        public InvocationScope(
            IServiceProvider services,
            IExtensionContext context,
            ExtensionInvocationResources resources
        )
        {
            Services = services;
            Context = context;
            Resources = resources;
        }

        public IServiceProvider Services { get; }

        public IExtensionContext Context { get; }

        public ExtensionInvocationResources Resources { get; }

        public ValueTask DisposeAsync() => Resources.DisposeAsync();
    }
}
