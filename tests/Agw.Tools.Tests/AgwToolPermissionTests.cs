using System.Runtime.CompilerServices;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class AgwToolPermissionTests
{
    [Theory]
    [InlineData(AgwToolPermission.None, false)]
    [InlineData(AgwToolPermission.ReadOnly, false)]
    [InlineData(AgwToolPermission.Write, true)]
    [InlineData(AgwToolPermission.Execute, true)]
    public void Bind_DeclaredPermission_AppliesExpectedApprovalRequirement(
        AgwToolPermission permission,
        bool requiresApproval
    )
    {
        // Arrange
        var function = AIFunctionFactory.Create((Func<string>)(() => "ok"), "test_tool");
        var metadata = new AgwToolMetadata("test", permission);

        // Act
        var result = Assert.IsAssignableFrom<AIFunction>(AgwToolMetadataBinding.Bind(function, metadata));

        // Assert
        Assert.Equal(metadata, result.GetService<AgwToolMetadata>());
        Assert.Equal(requiresApproval, result.GetService<ApprovalRequiredAIFunction>() is not null);
        if (requiresApproval)
        {
            Assert.IsType<ApprovalRequiredAIFunction>(result);
        }
    }

    [Fact]
    public async Task ContextProvider_DynamicTool_UsesDeclaredPermission()
    {
        // Arrange
        var function = AIFunctionFactory.Create((Func<string>)(() => "ok"), "member_write");
        var provider = new AgwToolMetadataContextProvider(
            new Dictionary<string, AgwToolMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [function.Name] = new("tool-block:test", AgwToolPermission.Write),
            }
        );
        var context = new AIContext { Tools = [function] };

        // Act
        var result = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(new TestAgent(), null, context),
            TestContext.Current.CancellationToken
        );

        // Assert
        var secured = Assert.IsAssignableFrom<AIFunction>(Assert.Single(result.Tools!));
        Assert.NotNull(secured.GetService<ApprovalRequiredAIFunction>());
        Assert.Equal(AgwToolPermission.Write, secured.GetService<AgwToolMetadata>()?.RequiredPermission);
    }

    [Fact]
    public async Task ContextProvider_DuplicateDynamicToolName_RejectsAmbiguousSource()
    {
        // Arrange
        var provider = new AgwToolMetadataContextProvider(
            new Dictionary<string, AgwToolMetadata> { ["duplicate"] = new("tool-block:test", AgwToolPermission.Write) }
        );
        var tools = new AITool[]
        {
            AIFunctionFactory.Create((Func<string>)(() => "one"), "duplicate"),
            AIFunctionFactory.Create((Func<string>)(() => "two"), "duplicate"),
        };

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await provider.InvokingAsync(
                new AIContextProvider.InvokingContext(new TestAgent(), null, new AIContext { Tools = tools }),
                TestContext.Current.CancellationToken
            )
        );

        // Assert
        Assert.Contains("produced more than once", exception.Message);
    }

    [Fact]
    public async Task ContextProvider_SourceProducesUndeclaredTool_RejectsOnlyNewSourceTools()
    {
        // Arrange
        var existing = AIFunctionFactory.Create((Func<string>)(() => "external"), "external_tool");
        var declared = AIFunctionFactory.Create((Func<string>)(() => "declared"), "declared_member");
        var undeclared = AIFunctionFactory.Create((Func<string>)(() => "undeclared"), "undeclared_member");
        var provider = new AgwToolMetadataContextProvider(
            [new ToolProducingProvider(declared, undeclared)],
            new Dictionary<string, AgwToolMetadata>
            {
                [declared.Name] = new("tool-block:test", AgwToolPermission.ReadOnly),
            }
        );

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await provider.InvokingAsync(
                new AIContextProvider.InvokingContext(new TestAgent(), null, new AIContext { Tools = [existing] }),
                TestContext.Current.CancellationToken
            )
        );

        // Assert
        Assert.Contains("undeclared members: undeclared_member", exception.Message);
    }

    private sealed class ToolProducingProvider : AIContextProvider
    {
        private readonly IReadOnlyList<AITool> _tools;

        public ToolProducingProvider(params AITool[] tools)
        {
            _tools = tools;
        }

        public override IReadOnlyList<string> StateKeys => [];

        protected override ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default
        )
        {
            context.AIContext.Tools = (context.AIContext.Tools ?? []).Concat(_tools).ToArray();
            return ValueTask.FromResult(context.AIContext);
        }
    }

    private sealed class TestAgent : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            yield break;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedSession,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}
