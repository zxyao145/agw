using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes;

namespace Agw.Agents.Tests;

public class ExecutionSettingsTests
{
    [Fact]
    public void DurableSettings_RoundTrip_PreservesUnattendedPermissions()
    {
        var settings = ExecutionSettings
            .CreateDefault()
            .WithPermissionMode(AgwPermissionMode.FullAccess)
            .WithHumanInteractionPolicy(HumanInteractionPolicy.Reject);
        var snapshot = DurableExecutionMapper.FromSettings(settings);

        var restored = JsonSerializer.Deserialize<DurableExecutionSettings>(JsonSerializer.Serialize(snapshot))!;

        Assert.Equal(
            AgwPermissionMode.FullAccess,
            restored.ToRuntimeSettings(Guid.CreateVersion7(), "context").PermissionMode
        );
        var runtimeSettings = restored.ToRuntimeSettings(Guid.CreateVersion7(), "context");
        Assert.Equal(HumanInteractionPolicy.Reject, runtimeSettings.HumanInteractionPolicy);
        Assert.Equal(settings.PermissionVersion, runtimeSettings.PermissionVersion);
        Assert.Equal(settings.Resume, runtimeSettings.Resume);
        Assert.Equal(
            HumanInteractionPolicy.Reject,
            settings.WithPermissionMode(AgwPermissionMode.AlwaysAsk).HumanInteractionPolicy
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

        var settings = SettingCommandMapper.FromCommand(command);
        command.EnvironmentVariables["TOKEN"] = "changed";

        Assert.Equal("original", settings.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public void Equals_WhenResumeDiffers_ReturnsFalse()
    {
        var projectId = Guid.CreateVersion7();
        var left = SettingCommandMapper.FromCommand(new SettingCommand(projectId) { Resume = false });
        var right = SettingCommandMapper.FromCommand(new SettingCommand(projectId) { Resume = true });

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void PermissionMode_IsPreservedAndParticipatesInEquality()
    {
        var projectId = Guid.CreateVersion7();
        var command = new SettingCommand(projectId, contextId: "context", permissionMode: AgwPermissionMode.FullAccess);

        var settings = SettingCommandMapper.FromCommand(command);

        Assert.Equal(AgwPermissionMode.FullAccess, settings.PermissionMode);
        Assert.NotEqual(
            settings,
            SettingCommandMapper.FromCommand(
                new SettingCommand(projectId, contextId: "context", permissionMode: AgwPermissionMode.AlwaysAsk)
            )
        );
        Assert.Equal("\"fullAccess\"", JsonSerializer.Serialize(AgwPermissionMode.FullAccess));
    }

    [Fact]
    public void ResultOnly_IsPreservedAndExcludedFromEquality()
    {
        var projectId = Guid.CreateVersion7();

        var settings = SettingCommandMapper.FromCommand(
            new SettingCommand(projectId, contextId: "context", resultOnly: true)
        );

        Assert.True(settings.ResultOnly);
        Assert.Equal(settings, SettingCommandMapper.FromCommand(new SettingCommand(projectId, contextId: "context")));
        Assert.False(settings.WithResultOnly(false).ResultOnly);
    }

    [Fact]
    public void ResultOnly_SurvivesDerivedSettingsAndDurableRoundTrip()
    {
        var settings = ExecutionSettings.CreateDefault().WithResultOnly(true);

        Assert.True(settings.WithPermissionMode(AgwPermissionMode.FullAccess).ResultOnly);
        Assert.True(settings.WithHumanInteractionPolicy(HumanInteractionPolicy.Reject).ResultOnly);

        var snapshot = DurableExecutionMapper.FromSettings(settings);
        var restored = JsonSerializer.Deserialize<DurableExecutionSettings>(JsonSerializer.Serialize(snapshot))!;

        Assert.True(restored.ToRuntimeSettings(Guid.CreateVersion7(), "context").ResultOnly);
    }
}
