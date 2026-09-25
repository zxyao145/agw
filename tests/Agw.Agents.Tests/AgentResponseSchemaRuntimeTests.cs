using System.Text.Json;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using ClaudeCodeSdk.MAF;
using Microsoft.Extensions.AI;
using OpenAI.CodexSdk.MAF;

namespace Agw.Agents.Tests;

public sealed class AgentResponseSchemaRuntimeTests
{
    private const string Schema =
        """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""";

    [Fact]
    public void Create_WithoutSchema_KeepsExistingChatOptions()
    {
        Assert.Null(AgentResponseSchemaFormat.Create(new Agent { Name = "agent" }, ProviderType.OpenAIChatCompletions));
        Assert.Null(
            AgentResponseSchemaFormat.Create(
                new Agent { Name = "agent", ResponseSchema = "  " },
                ProviderType.OpenAIChatCompletions
            )
        );
    }

    [Fact]
    public void Create_ValidSchema_BuildsJsonSchemaFormatWithSanitizedName()
    {
        var format = AgentResponseSchemaFormat.Create(
            new Agent { Name = "my agent!", ResponseSchema = Schema },
            ProviderType.OpenAIChatCompletions
        );

        var jsonSchema = Assert.IsType<ChatResponseFormatJson>(format);
        Assert.Equal("my_agent_", jsonSchema.SchemaName);
        Assert.Equal(JsonValueKind.Object, jsonSchema.Schema!.Value.ValueKind);
        Assert.True(jsonSchema.Schema.Value.TryGetProperty("required", out _));
    }

    [Fact]
    public void Create_UnusableName_FallsBackToStableSchemaName()
    {
        var format = AgentResponseSchemaFormat.Create(
            new Agent { Name = "中文", ResponseSchema = Schema },
            ProviderType.OpenAIChatCompletions
        );

        Assert.Equal("response_schema", Assert.IsType<ChatResponseFormatJson>(format).SchemaName);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    public void Create_InvalidPersistedSchema_Fails(string schema) =>
        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            Assert
                .Throws<AgwException>(() =>
                    AgentResponseSchemaFormat.Create(
                        new Agent { Name = "agent", ResponseSchema = schema },
                        ProviderType.OpenAIChatCompletions
                    )
                )
                .Code
        );

    [Fact]
    public void ApplyResponseSchema_ClaudeCode_BlankSchema_KeepsOptionsUnchanged()
    {
        var options = new ClaudeCodeAIAgentOptions();

        Assert.Same(options, AgentRuntimeFactory.ApplyResponseSchema(options, "  "));
    }

    [Fact]
    public void ApplyResponseSchema_ClaudeCode_WinsOverExtraSettingsAndPreservesOtherArgs()
    {
        var options = new ClaudeCodeAIAgentOptions
        {
            ExtraArgs = new Dictionary<string, string?> { ["json-schema"] = "{\"old\":true}", ["model"] = "sonnet" },
        };

        var updated = AgentRuntimeFactory.ApplyResponseSchema(options, Schema);

        Assert.Equal(Schema, updated.ExtraArgs!["json-schema"]);
        Assert.Equal("sonnet", updated.ExtraArgs["model"]);
    }

    [Fact]
    public void ApplyResponseSchema_ClaudeCode_PreservesResumeState()
    {
        var options = new ClaudeCodeAIAgentOptions { Resume = "session-1" };

        var updated = AgentRuntimeFactory.ApplyResponseSchema(options, Schema);

        Assert.Equal("session-1", updated.Resume);
        Assert.Equal(Schema, updated.ExtraArgs!["json-schema"]);
    }

    [Fact]
    public void ApplyCodexResponseSchema_PreservesThreadResumeState()
    {
        var threadId = Guid.CreateVersion7();
        var options = new CodexAIAgentOptions { ThreadId = threadId, IsResume = true };

        var updated = AgentRuntimeFactory.ApplyCodexResponseSchema(options, Schema);

        Assert.Equal(threadId, updated.ThreadId);
        Assert.True(updated.IsResume);
        Assert.NotNull(updated.TurnOptions?.OutputSchema);
    }

    [Fact]
    public void ApplyCodexResponseSchema_SetsOutputSchemaOnTurnOptions()
    {
        var options = AgentRuntimeFactory.ApplyCodexResponseSchema(new CodexAIAgentOptions(), Schema);

        Assert.NotNull(options.TurnOptions);
        var outputSchema = options.TurnOptions!.OutputSchema;
        Assert.NotNull(outputSchema);
        Assert.Equal("object", Assert.IsType<JsonElement>(outputSchema!["type"]).GetString());
        Assert.Equal(JsonValueKind.Object, Assert.IsType<JsonElement>(outputSchema["properties"]).ValueKind);
    }

    [Fact]
    public void ApplyCodexResponseSchema_BlankSchema_KeepsTurnOptionsUnset()
    {
        var options = new CodexAIAgentOptions();

        Assert.Same(options, AgentRuntimeFactory.ApplyCodexResponseSchema(options, " "));
        Assert.Null(options.TurnOptions);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    public void ApplyCodexResponseSchema_InvalidSchema_Fails(string schema) =>
        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            Assert
                .Throws<AgwException>(() =>
                    AgentRuntimeFactory.ApplyCodexResponseSchema(new CodexAIAgentOptions(), schema)
                )
                .Code
        );
}
