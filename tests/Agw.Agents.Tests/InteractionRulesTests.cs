using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class InteractionRulesTests
{
    [Theory]
    [InlineData(AgwPermissionMode.AlwaysAsk, ApprovalScope.Once)]
    [InlineData(AgwPermissionMode.AllowSameArguments, ApprovalScope.AlwaysArguments)]
    [InlineData(AgwPermissionMode.FullAccess, ApprovalScope.AlwaysTool)]
    public void ValidateAndNormalize_ApprovedTool_EnforcesMode(AgwPermissionMode mode, ApprovalScope scope)
    {
        var request = InteractionTestData.Tool("tool");
        var result = Assert.IsType<ToolApprovalDecision>(
            InteractionRules.ValidateAndNormalize(
                request,
                new ToolApprovalDecision
                {
                    InteractionId = "tool",
                    Approved = true,
                    Scope = ApprovalScope.AlwaysTool,
                },
                mode
            )
        );
        Assert.Equal(scope, result.Scope);
    }

    [Theory]
    [InlineData("tool", AgwPermissionMode.AlwaysAsk, false)]
    [InlineData("tool", AgwPermissionMode.AllowSameArguments, false)]
    [InlineData("tool", AgwPermissionMode.FullAccess, true)]
    [InlineData("granted-tool", AgwPermissionMode.AlwaysAsk, true)]
    [InlineData("granted-tool", AgwPermissionMode.AllowSameArguments, true)]
    [InlineData("granted-tool", AgwPermissionMode.FullAccess, true)]
    [InlineData("input", AgwPermissionMode.AlwaysAsk, false)]
    [InlineData("input", AgwPermissionMode.AllowSameArguments, false)]
    [InlineData("input", AgwPermissionMode.FullAccess, false)]
    [InlineData("gate", AgwPermissionMode.AlwaysAsk, false)]
    [InlineData("gate", AgwPermissionMode.AllowSameArguments, false)]
    [InlineData("gate", AgwPermissionMode.FullAccess, false)]
    public void AutomaticallyApprove_RequestKindAndMode_ApprovesOnlyFullAccessOrGrantedTools(
        string kind,
        AgwPermissionMode mode,
        bool expected
    )
    {
        InteractionRequest request = kind switch
        {
            "tool" or "granted-tool" => InteractionTestData.Tool(kind),
            "input" => InteractionTestData.Input(kind),
            _ => InteractionTestData.Gate(kind),
        };

        // 输入与 HumanGate 即使带有授权也需要用户。
        // Input and HumanGate need the user even with a grant.
        var decision = InteractionRules.AutomaticallyApprove(request, mode, granted: kind != "tool");

        Assert.Equal(expected, decision != null);
        if (decision != null)
        {
            Assert.True(decision.Approved);
            Assert.Equal(ApprovalScope.Once, decision.Scope);
            Assert.Equal(request.InteractionId, decision.InteractionId);
        }
    }

    [Fact]
    public async Task UnattendedHandler_EveryRequestKind_FailsExecution()
    {
        var handler = new UnattendedInteractionHandler();

        foreach (
            var request in new InteractionRequest[]
            {
                InteractionTestData.Tool("tool"),
                InteractionTestData.Gate("gate"),
                InteractionTestData.Input("input"),
            }
        )
        {
            var error = await Assert.ThrowsAsync<AgwException>(async () =>
                await handler.ResolveAsync(request, TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, error.Code);
        }
    }

    [Fact]
    public void ValidateAndNormalize_UnknownScope_RejectsResponse()
    {
        Assert.Throws<AgwException>(() =>
            InteractionRules.ValidateAndNormalize(
                InteractionTestData.Tool("tool"),
                new ToolApprovalDecision
                {
                    InteractionId = "tool",
                    Approved = true,
                    Scope = (ApprovalScope)999,
                },
                null
            )
        );
    }
}
