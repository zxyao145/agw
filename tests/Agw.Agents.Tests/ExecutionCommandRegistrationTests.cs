using System.Text.Json;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Commands.Checkpoint;
using Agw.Agents.Execution.Commands.Mode;
using Agw.Agents.Execution.Commands.Permission;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests;

public class ExecutionCommandRegistrationTests
{
    [Fact]
    public async Task AddExecutionCommand_RegistersJsonDiscriminatorAndTypedHandlerTogether()
    {
        var services = new ServiceCollection();
        services.AddExecutionCommand<StatusCommand, StatusCommandHandler>("StatusCommand");
        services.AddScoped<ExecutionCommandDispatcher>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;
        const string json = """
            {
              "type": "StatusCommand",
              "includeDetails": true
            }
            """;
        var command = JsonSerializer.Deserialize<AgentRunCommand>(json, options);
        var handler = scope.ServiceProvider.GetRequiredService<IExecutionCommandHandler<StatusCommand>>();

        await scope
            .ServiceProvider.GetRequiredService<ExecutionCommandDispatcher>()
            .DispatchAsync(command!, context: null!, TestContext.Current.CancellationToken);

        Assert.True(Assert.IsType<StatusCommand>(command).IncludeDetails);
        Assert.Same(command, Assert.IsType<StatusCommandHandler>(handler).Command);
    }

    [Fact]
    public void AddExecutionCommand_DuplicateDiscriminator_FailsOptionsValidation()
    {
        var services = new ServiceCollection();
        services.AddExecutionCommand<StatusCommand, StatusCommandHandler>("duplicate");
        services.AddExecutionCommand<OtherCommand, OtherCommandHandler>("duplicate");
        using var provider = services.BuildServiceProvider();

        Assert.Throws<AgwException>(() => provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value);
    }

    [Fact]
    public void AddExecutionCommands_RegistersSetModeCommand()
    {
        var services = new ServiceCollection();
        services.AddExecutionCommands();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        var command = JsonSerializer.Deserialize<AgentRunCommand>(
            """
            { "type": "SetModeCommand", "agentId": "0190c7e9-19f3-7fb5-8c16-21b70989f001", "mode": "plan" }
            """,
            options
        );

        var setMode = Assert.IsType<SetModeCommand>(command);
        Assert.Equal("plan", setMode.Mode);
    }

    [Fact]
    public void AddExecutionCommands_RegistersSetPermissionModeCommand()
    {
        var services = new ServiceCollection();
        services.AddExecutionCommands();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        var command = JsonSerializer.Deserialize<AgentRunCommand>(
            """
            { "type": "SetPermissionModeCommand", "permissionMode": "allowSameArguments" }
            """,
            options
        );

        var setPermissionMode = Assert.IsType<SetPermissionModeCommand>(command);
        Assert.Equal(PermissionMode.AllowSameArguments, setPermissionMode.PermissionMode);
    }

    [Fact]
    public void AddExecutionCommands_RegistersResumeCheckpointCommand()
    {
        var services = new ServiceCollection();
        services.AddExecutionCommands();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        var command = JsonSerializer.Deserialize<AgentRunCommand>(
            """
            {
              "type": "ResumeCheckpointCommand",
              "checkpointOccurrenceId": "0190c7e9-19f3-7fb5-8c16-21b70989f001",
              "resumeExecutionId": "0190c7e9-19f3-7fb5-8c16-21b70989f002",
              "agentflowId": "0190c7e9-19f3-7fb5-8c16-21b70989f003"
            }
            """,
            options
        );

        var resume = Assert.IsType<ResumeCheckpointCommand>(command);
        Assert.Equal(Guid.Parse("0190c7e9-19f3-7fb5-8c16-21b70989f001"), resume.CheckpointOccurrenceId);
    }

    [Fact]
    public void RuntimeTurnContextAccessorContract_IsReadOnly()
    {
        Assert.Single(typeof(IRuntimeTurnContextAccessor).GetProperties());
        Assert.DoesNotContain(typeof(IRuntimeTurnContextAccessor).GetMethods(), method => method.Name == "Push");
    }

    private sealed class StatusCommand : AgentRunCommand
    {
        public bool IncludeDetails { get; set; }
    }

    private sealed class OtherCommand : AgentRunCommand;

    private sealed class StatusCommandHandler : IExecutionCommandHandler<StatusCommand>
    {
        public StatusCommand? Command { get; private set; }

        public Task HandleAsync(
            StatusCommand command,
            ExecutionConnectionContext context,
            CancellationToken cancellationToken
        )
        {
            Command = command;
            return Task.CompletedTask;
        }
    }

    private sealed class OtherCommandHandler : IExecutionCommandHandler<OtherCommand>
    {
        public Task HandleAsync(
            OtherCommand command,
            ExecutionConnectionContext context,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
