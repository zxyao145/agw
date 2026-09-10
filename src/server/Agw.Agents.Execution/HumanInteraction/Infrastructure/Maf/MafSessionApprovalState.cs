using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Execution.HumanInteraction.Application;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

internal static class MafSessionApprovalState
{
    internal const string GrantStateKey = "Agw.ToolApproval.Grants";

    internal static AgwPermissionMode? GetPermissionMode(AgentSession session) =>
        session.StateBag.TryGetValue<MafSessionGrantState>(GrantStateKey, out var state, JsonSerializerOptions.Default)
            ? state?.PermissionMode
            : null;

    // Session state is accessed only by its SDK execution flow, never by the control monitor.
    public static void Apply(AgentSession session, AgwPermissionMode? permissionMode) =>
        GetOrCreateState(session, permissionMode);

    internal static AgwPermissionMode? Synchronize(AgentSession session, InteractionPermissionState permissions)
    {
        var snapshot = permissions.Snapshot;
        var state = GetOrCreateState(session, snapshot.Mode);
        if (state.PermissionVersion != snapshot.Version)
            state.Grants.Clear();
        state.PermissionScopeId = permissions.ScopeId;
        state.PermissionVersion = snapshot.Version;
        session.StateBag.SetValue(GrantStateKey, state, JsonSerializerOptions.Default);
        return snapshot.Mode;
    }

    public static void Record(
        AgentSession session,
        FunctionCallContent functionCall,
        ApprovalScope scope,
        AgwPermissionMode? permissionMode
    )
    {
        ArgumentNullException.ThrowIfNull(functionCall);
        var state = GetOrCreateState(session, permissionMode);
        if (
            permissionMode == AgwPermissionMode.AlwaysAsk
            || scope is not (ApprovalScope.AlwaysTool or ApprovalScope.AlwaysArguments)
        )
        {
            return;
        }

        if (permissionMode == AgwPermissionMode.AllowSameArguments)
        {
            scope = ApprovalScope.AlwaysArguments;
        }

        var arguments =
            scope == ApprovalScope.AlwaysArguments
                ? JsonSerializer.SerializeToNode(functionCall.Arguments, JsonSerializerOptions.Default)
                : null;
        if (
            state.Grants.Any(grant =>
                string.Equals(grant.ToolName, functionCall.Name, StringComparison.Ordinal)
                && (
                    grant.Scope == ApprovalScope.AlwaysTool
                    || (scope == ApprovalScope.AlwaysArguments && JsonNode.DeepEquals(grant.Arguments, arguments))
                )
            )
        )
        {
            return;
        }

        state.Grants.Add(
            new MafToolGrant
            {
                ToolName = functionCall.Name,
                Scope = scope,
                Arguments = arguments,
            }
        );
        session.StateBag.SetValue(GrantStateKey, state, JsonSerializerOptions.Default);
    }

    public static bool TryApprove(ToolAutoApprovalRuleContext context, AgwPermissionMode? permissionMode)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Session is not { } session)
        {
            return false;
        }

        var state = GetOrCreateState(session, permissionMode);
        if (permissionMode == AgwPermissionMode.AlwaysAsk)
        {
            return false;
        }

        var functionCall = context.FunctionCallContent;
        var arguments = JsonSerializer.SerializeToNode(functionCall.Arguments, JsonSerializerOptions.Default);
        return state.Grants.Any(grant =>
            string.Equals(grant.ToolName, functionCall.Name, StringComparison.Ordinal)
            && (
                (grant.Scope == ApprovalScope.AlwaysTool && permissionMode != AgwPermissionMode.AllowSameArguments)
                || (grant.Scope == ApprovalScope.AlwaysArguments && JsonNode.DeepEquals(grant.Arguments, arguments))
            )
        );
    }

    private static MafSessionGrantState GetOrCreateState(AgentSession session, AgwPermissionMode? permissionMode)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (
            !session.StateBag.TryGetValue<MafSessionGrantState>(
                GrantStateKey,
                out var state,
                JsonSerializerOptions.Default
            ) || state is null
        )
        {
            state = new MafSessionGrantState { PermissionMode = permissionMode };
            session.StateBag.SetValue(GrantStateKey, state, JsonSerializerOptions.Default);
        }
        else if (state.PermissionMode != permissionMode)
        {
            state.PermissionMode = permissionMode;
            state.Grants.Clear();
            session.StateBag.SetValue(GrantStateKey, state, JsonSerializerOptions.Default);
        }

        return state;
    }
}
