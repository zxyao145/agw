using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.AIContextProviders.PlanMode;

internal sealed class PlanModeToolGuardProvider : AIContextProvider
{
    internal const string EnforcementInstructions = """
        ## Plan Mode Tool Enforcement
        Plan mode is enforced by the server. Only the tools currently exposed to you may be used.
        Todo tools may be used to organize planning. Do not attempt shell commands; file or project-memory mutations; skill scripts; background-task mutations; or external Connection/MCP tools.
        Inspect and research existing data, ask clarifying questions when needed, then present the plan in chat and wait for explicit human approval before requesting a switch to Execute mode.
        """;

    private readonly AgentModeProvider _modeProvider;
    private readonly IReadOnlySet<string> _allowedToolNames;
    private readonly ILogger _logger;

    public PlanModeToolGuardProvider(
        AgentModeProvider modeProvider,
        IReadOnlySet<string> allowedToolNames,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(modeProvider);
        ArgumentNullException.ThrowIfNull(allowedToolNames);

        _modeProvider = modeProvider;
        _allowedToolNames = new HashSet<string>(allowedToolNames, StringComparer.OrdinalIgnoreCase);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public override IReadOnlyList<string> StateKeys => [];

    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        var executeMode = await IsExecuteModeAsync(_modeProvider, context.Session, _logger, cancellationToken)
            .ConfigureAwait(false);
        var tools = context.AIContext.Tools;
        var duplicateNames =
            tools == null
                ? []
                : tools
                    .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
                    .GroupBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(static group => group.Skip(1).Any())
                    .Select(static group => group.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (executeMode)
        {
            if (tools != null)
            {
                context.AIContext.Tools = tools
                    .Select(tool => WrapIfRestricted(tool, context.Session, duplicateNames.Contains(tool.Name)))
                    .ToArray();
            }

            return context.AIContext;
        }

        if (tools != null)
        {
            context.AIContext.Tools = tools
                .Where(tool =>
                    !duplicateNames.Contains(tool.Name) && (_allowedToolNames.Contains(tool.Name) || tool is AIFunction)
                )
                .Select(tool =>
                    _allowedToolNames.Contains(tool.Name)
                        ? tool
                        : WrapRestrictedFunction(tool, context.Session, hideFromModel: true)
                )
                .ToArray();
        }

        context.AIContext.Instructions = string.IsNullOrWhiteSpace(context.AIContext.Instructions)
            ? EnforcementInstructions
            : $"{context.AIContext.Instructions}\n\n{EnforcementInstructions}";
        return context.AIContext;
    }

    private AITool WrapIfRestricted(AITool tool, AgentSession? session, bool hasNameConflict)
    {
        if ((!hasNameConflict && _allowedToolNames.Contains(tool.Name)) || tool is not AIFunction)
        {
            return tool;
        }

        return WrapRestrictedFunction(tool, session, hideFromModel: false);
    }

    private AITool WrapRestrictedFunction(AITool tool, AgentSession? session, bool hideFromModel)
    {
        var function = (AIFunction)tool;
        if (function is PlanModeRestrictedAIFunction existing && existing.HideFromModel == hideFromModel)
        {
            return function;
        }

        return new PlanModeRestrictedAIFunction(function, _modeProvider, session, _logger, hideFromModel);
    }

    internal static async ValueTask<bool> IsExecuteModeAsync(
        AgentModeProvider modeProvider,
        AgentSession? session,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        if (session == null)
        {
            return false;
        }

        try
        {
            var mode = await modeProvider.GetModeAsync(session, cancellationToken).ConfigureAwait(false);
            return string.Equals(mode, "execute", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to resolve Agent mode; applying Plan mode restrictions.");
            return false;
        }
    }
}

internal sealed class PlanModeRestrictedAIFunction : DelegatingAIFunction
{
    private readonly AgentModeProvider _modeProvider;
    private readonly AgentSession? _session;
    private readonly ILogger _logger;

    public PlanModeRestrictedAIFunction(
        AIFunction innerFunction,
        AgentModeProvider modeProvider,
        AgentSession? session,
        ILogger logger,
        bool hideFromModel
    )
        : base(innerFunction)
    {
        _modeProvider = modeProvider;
        _session = session;
        _logger = logger;
        HideFromModel = hideFromModel;
    }

    internal bool HideFromModel { get; }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (HideFromModel && serviceType == typeof(ApprovalRequiredAIFunction))
        {
            return null;
        }

        return base.GetService(serviceType, serviceKey);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        if (
            !await PlanModeToolGuardProvider
                .IsExecuteModeAsync(_modeProvider, _session, _logger, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            var errorCode = ErrorCodes.PlanModeToolNotAllowed.Code;
            return $"{errorCode / 10_000:D3}_{errorCode % 10_000:D4} "
                + $"PlanModeToolNotAllowed: Tool '{Name}' is not available in Plan mode.";
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
