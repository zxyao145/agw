using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Commands.Checkpoint;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Commands.Interrupt;
using Agw.Agents.Execution.Commands.Mode;
using Agw.Agents.Execution.Commands.Permission;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Commands.Subscribe;
using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Commands;

internal sealed record ExecutionCommandRegistration(Type CommandType, string Discriminator);

public static class ExecutionCommandRegistrationExtensions
{
    public static IServiceCollection AddExecutionCommands(this IServiceCollection services) =>
        services
            .AddExecutionCommand<SettingCommand, SettingCommandHandler>(nameof(SettingCommand))
            .AddExecutionCommand<ExecCommand, ExecCommandHandler>(nameof(ExecCommand))
            .AddExecutionCommand<InterruptCommand, InterruptCommandHandler>(nameof(InterruptCommand))
            .AddExecutionCommand<SetModeCommand, SetModeCommandHandler>(nameof(SetModeCommand))
            .AddExecutionCommand<SetPermissionModeCommand, SetPermissionModeCommandHandler>(
                nameof(SetPermissionModeCommand))
            .AddExecutionCommand<SubscribeExecutionCommand, SubscribeExecutionCommandHandler>(
                nameof(SubscribeExecutionCommand))
            .AddExecutionCommand<ResumeCheckpointCommand, ResumeCheckpointCommandHandler>(
                nameof(ResumeCheckpointCommand))
            .AddExecutionCommand<HumanResponseCommand, HumanResponseCommandHandler>(nameof(HumanResponseCommand));

    public static IServiceCollection AddExecutionCommand<TCommand, THandler>(
        this IServiceCollection services,
        string discriminator)
        where TCommand : AgentRunCommand
        where THandler : class, IExecutionCommandHandler<TCommand>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        services.AddOptions();
        services.AddScoped<IExecutionCommandHandler<TCommand>, THandler>();
        services.AddScoped<IExecutionCommandHandler>(serviceProvider =>
            new ExecutionCommandHandlerAdapter<TCommand>(
                serviceProvider.GetRequiredService<IExecutionCommandHandler<TCommand>>()));
        services.AddSingleton(new ExecutionCommandRegistration(typeof(TCommand), discriminator));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IConfigureOptions<JsonHubProtocolOptions>,
                ExecutionCommandJsonProtocolOptionsSetup>());
        return services;
    }
}

internal sealed class ExecutionCommandJsonProtocolOptionsSetup : IConfigureOptions<JsonHubProtocolOptions>
{
    private readonly IReadOnlyList<ExecutionCommandRegistration> _registrations;

    public ExecutionCommandJsonProtocolOptionsSetup(
        IEnumerable<ExecutionCommandRegistration> registrations)
    {
        _registrations = registrations.ToArray();
    }

    public void Configure(JsonHubProtocolOptions options) =>
        ExecutionCommandJson.Configure(options.PayloadSerializerOptions, _registrations);
}

internal static class ExecutionCommandJson
{
    public static void Configure(
        JsonSerializerOptions options,
        IEnumerable<ExecutionCommandRegistration> registrations)
    {
        var registrationsByType = new Dictionary<Type, ExecutionCommandRegistration>();
        var discriminators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            if (!registrationsByType.TryAdd(registration.CommandType, registration))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Multiple execution commands are registered for '{registration.CommandType.Name}'.");
            }

            if (!discriminators.Add(registration.Discriminator))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Multiple execution commands use discriminator '{registration.Discriminator}'.");
            }
        }

        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type != typeof(AgentRunCommand))
            {
                return;
            }

            var polymorphism = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "type",
            };
            foreach (var registration in registrationsByType.Values)
            {
                polymorphism.DerivedTypes.Add(
                    new JsonDerivedType(registration.CommandType, registration.Discriminator));
            }

            typeInfo.PolymorphismOptions = polymorphism;
        });
        options.TypeInfoResolverChain.Insert(0, resolver);
        options.AllowOutOfOrderMetadataProperties = true;
    }
}
