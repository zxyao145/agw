using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Infrastructure.Runtime;

public sealed partial class ExtensionRuntime
{
    public async ValueTask<ExtensionEventHandlerResult> InvokeEventHandlerAsync(
        IExtensionGenerationLease generationLease,
        ExtensionEventHandlerInvocation invocation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(invocation);
        (GenerationLease lease, ExtensionGeneration generation) = GetUsableGenerationLease(generationLease);
        if (invocation.Delivery is null)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        ValidateUserAndInvocation(invocation.UserId, invocation.Delivery.DeliveryId);
        RegisteredEventHandler handler =
            generation.Registration.EventHandlers.FirstOrDefault(candidate =>
                candidate.Descriptor.Id == invocation.HandlerId
            ) ?? throw new AgwException(ErrorCodes.InvalidParam);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: false,
            backgroundWork: true,
            token => _availability.CanAcceptNewWorkAsync(lease.Pin, token),
            cancellationToken,
            invocation.UserId,
            invocation.Delivery.DeliveryId
        );
        await using (scope)
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            try
            {
                ExtensionEventHandlerResult result = await handler.Handler(
                    scope.Services,
                    scope.Context,
                    invocation.Delivery,
                    cancellationToken
                );
                ValidateEventHandlerResult(result);
                return result;
            }
            catch (Exception exception)
            {
                RethrowSafeException(exception, cancellationToken);
                _logger.LogError(
                    exception,
                    "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} event handler {HandlerId} failed",
                    generation.GenerationId.ExtensionId,
                    generation.Request.ReleaseHash,
                    generation.GenerationId.GenerationId,
                    handler.Descriptor.Id
                );
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }
        }
    }

    public async ValueTask<ExtensionSourcePollResult> InvokeSourcePollAsync(
        IExtensionGenerationLease generationLease,
        ExtensionSourcePollInvocation invocation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(invocation);
        (GenerationLease lease, ExtensionGeneration generation) = GetUsableGenerationLease(generationLease);
        ValidateUserAndInvocation(invocation.UserId, invocation.InvocationId);
        RegisteredEventSourceProvider providerRegistration =
            generation.Registration.EventSourceProviders.FirstOrDefault(candidate =>
                candidate.Descriptor.Id == invocation.ProviderId
            ) ?? throw new AgwException(ErrorCodes.InvalidParam);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: false,
            backgroundWork: true,
            token => _availability.CanAcceptNewWorkAsync(lease.Pin, token),
            cancellationToken,
            invocation.UserId,
            invocation.InvocationId
        );
        await using (scope)
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            try
            {
                IExtensionEventSourceProvider provider = (IExtensionEventSourceProvider)
                    scope.Services.GetRequiredService(providerRegistration.ProviderType);
                ExtensionSourcePollRequest request = invocation.Request with
                {
                    Config = invocation.Request.Config.Clone(),
                    Cursor = invocation.Request.Cursor?.Clone(),
                };
                await provider.ValidateConfigAsync(request.Config, cancellationToken);
                ExtensionSourcePollResult result = await provider.PollAsync(request, cancellationToken);
                if (result is null || result.Events is null || result.Events.Any(item => item is null))
                {
                    throw new AgwException(ErrorCodes.ExtensionUnavailable);
                }

                return result with
                {
                    Events = result.Events.ToArray(),
                    Cursor = result.Cursor?.Clone(),
                };
            }
            catch (Exception exception)
            {
                RethrowSafeException(exception, cancellationToken);
                _logger.LogError(
                    exception,
                    "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} event source provider {ProviderId} failed",
                    generation.GenerationId.ExtensionId,
                    generation.Request.ReleaseHash,
                    generation.GenerationId.GenerationId,
                    providerRegistration.Descriptor.Id
                );
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }
        }
    }

    public async ValueTask<ExtensionNotificationChannelResult> InvokeNotificationChannelAsync(
        IExtensionGenerationLease generationLease,
        ExtensionNotificationChannelInvocation invocation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(invocation);
        (GenerationLease lease, ExtensionGeneration generation) = GetUsableGenerationLease(generationLease);
        ValidateUserAndInvocation(invocation.UserId, invocation.InvocationId);
        RegisteredNotificationChannel channelRegistration =
            generation.Registration.NotificationChannels.FirstOrDefault(candidate =>
                candidate.Descriptor.Id == invocation.ChannelId
            ) ?? throw new AgwException(ErrorCodes.InvalidParam);
        ValidateNotificationChannelInvocation(invocation);
        InvocationScope scope = await BeginInvocationAsync(
            generation,
            allowDraining: false,
            backgroundWork: true,
            token => _availability.CanAcceptNewWorkAsync(lease.Pin, token),
            cancellationToken,
            invocation.UserId,
            invocation.InvocationId
        );
        await using (scope)
        {
            using IDisposable ambientContext = scope.Resources.EnterAmbientContext();
            try
            {
                IExtensionNotificationChannel channel = (IExtensionNotificationChannel)
                    scope.Services.GetRequiredService(channelRegistration.ChannelType);
                ExtensionNotificationChannelResult result = invocation.Operation switch
                {
                    ExtensionNotificationChannelOperation.ListTargets => new ExtensionNotificationChannelResult
                    {
                        Targets = (await channel.ListTargetsAsync(cancellationToken)).ToArray(),
                    },
                    ExtensionNotificationChannelOperation.Send => new ExtensionNotificationChannelResult
                    {
                        SendResult = await channel.SendAsync(invocation.Message!, cancellationToken),
                    },
                    ExtensionNotificationChannelOperation.Update => new ExtensionNotificationChannelResult
                    {
                        SendResult = await channel.UpdateAsync(
                            invocation.Message!,
                            invocation.ExternalMessageId!,
                            cancellationToken
                        ),
                    },
                    _ => throw new AgwException(ErrorCodes.InvalidParam),
                };
                ValidateNotificationChannelResult(invocation.Operation, result);
                return result;
            }
            catch (Exception exception)
            {
                RethrowSafeException(exception, cancellationToken);
                _logger.LogError(
                    exception,
                    "Extension {ExtensionId} release {ReleaseHash} generation {GenerationId} notification channel {ChannelId} failed",
                    generation.GenerationId.ExtensionId,
                    generation.Request.ReleaseHash,
                    generation.GenerationId.GenerationId,
                    channelRegistration.Descriptor.Id
                );
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }
        }
    }

    private static void ValidateUserAndInvocation(string userId, Guid invocationId)
    {
        if (string.IsNullOrWhiteSpace(userId) || invocationId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    private static void ValidateEventHandlerResult(ExtensionEventHandlerResult result)
    {
        if (result is null || !Enum.IsDefined(result.Kind))
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        if (
            result.Kind == ExtensionEventHandlerResultKind.Retry
            && (
                result.RetryAfter is null
                || result.RetryAfter <= TimeSpan.Zero
                || string.IsNullOrWhiteSpace(result.Reason)
            )
        )
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        if (result.Kind == ExtensionEventHandlerResultKind.Failed && string.IsNullOrWhiteSpace(result.Reason))
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    private static void ValidateNotificationChannelInvocation(ExtensionNotificationChannelInvocation invocation)
    {
        bool valid = invocation.Operation switch
        {
            ExtensionNotificationChannelOperation.ListTargets => invocation.Message is null
                && invocation.ExternalMessageId is null,
            ExtensionNotificationChannelOperation.Send => invocation.Message is not null
                && invocation.ExternalMessageId is null,
            ExtensionNotificationChannelOperation.Update => invocation.Message is not null
                && !string.IsNullOrWhiteSpace(invocation.ExternalMessageId),
            _ => false,
        };
        if (!valid)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    private static void ValidateNotificationChannelResult(
        ExtensionNotificationChannelOperation operation,
        ExtensionNotificationChannelResult result
    )
    {
        if (result is null)
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        if (operation == ExtensionNotificationChannelOperation.ListTargets)
        {
            if (result.Targets is null || result.SendResult is not null || result.Targets.Any(target => target is null))
            {
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }

            return;
        }

        if (result.SendResult is null || result.Targets is not null || !Enum.IsDefined(result.SendResult.Status))
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }
}
