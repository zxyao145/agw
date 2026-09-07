using System.Text.Json;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;

namespace Agw.Agents.Tests;

public class ExecutionSettingsTests
{
    [Fact]
    public void DurableSettings_RoundTrip_PreservesUnattendedPermissions()
    {
        var settings = ExecutionSettings
            .CreateDefault()
            .WithPermissionMode(PermissionMode.FullAccess)
            .WithHumanInteractionPolicy(HumanInteractionPolicy.Reject);
        var snapshot = DurableExecutionSettings.FromSettings(settings);

        var restored = JsonSerializer.Deserialize<DurableExecutionSettings>(JsonSerializer.Serialize(snapshot))!;

        Assert.Equal(PermissionMode.FullAccess, restored.ToCommand(Guid.CreateVersion7(), "context").PermissionMode);
        Assert.Equal(HumanInteractionPolicy.Reject, restored.HumanInteractionPolicy);
        Assert.Equal(
            HumanInteractionPolicy.Reject,
            settings.WithPermissionMode(PermissionMode.AlwaysAsk).HumanInteractionPolicy
        );
        Assert.NotEqual(settings, settings.WithHumanInteractionPolicy(HumanInteractionPolicy.Allow));
    }

    [Fact]
    public void DurableSettings_LegacyPayload_PreservesDefaults()
    {
        var restored = JsonSerializer.Deserialize<DurableExecutionSettings>(
            """{"EnvironmentVariables":{},"Resume":false}"""
        )!;

        Assert.Null(restored.PermissionMode);
        Assert.Equal(HumanInteractionPolicy.Allow, restored.HumanInteractionPolicy);
    }

    [Fact]
    public void FromCommand_CopiesMutableEnvironmentVariables()
    {
        var command = new SettingCommand(
            Guid.CreateVersion7(),
            new Dictionary<string, string> { ["TOKEN"] = "original" }
        );

        var settings = ExecutionSettings.FromCommand(command);
        command.EnvironmentVariables["TOKEN"] = "changed";

        Assert.Equal("original", settings.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public void Equals_WhenResumeDiffers_ReturnsFalse()
    {
        var projectId = Guid.CreateVersion7();
        var left = ExecutionSettings.FromCommand(new SettingCommand(projectId) { Resume = false });
        var right = ExecutionSettings.FromCommand(new SettingCommand(projectId) { Resume = true });

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void PermissionMode_RoundTripsAndParticipatesInEquality()
    {
        var projectId = Guid.CreateVersion7();
        var command = new SettingCommand(projectId, contextId: "context", permissionMode: PermissionMode.FullAccess);

        var settings = ExecutionSettings.FromCommand(command);
        var roundTripped = settings.ToCommand();

        Assert.Equal(PermissionMode.FullAccess, settings.PermissionMode);
        Assert.Equal(PermissionMode.FullAccess, roundTripped.PermissionMode);
        Assert.NotEqual(
            settings,
            ExecutionSettings.FromCommand(
                new SettingCommand(projectId, contextId: "context", permissionMode: PermissionMode.AlwaysAsk)
            )
        );
        Assert.Equal("\"fullAccess\"", JsonSerializer.Serialize(PermissionMode.FullAccess));
    }
}
