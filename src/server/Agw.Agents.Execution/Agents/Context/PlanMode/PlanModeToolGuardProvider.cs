using Agw.Shared.Exceptions;
using Agw.Tools.Impl.ToolBlocks.Mode;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Context.PlanMode;

internal sealed class PlanModeToolGuardProvider : AIContextProvider
{
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
        if (context.AIContext.Tools is { } tools)
        {
            var duplicateNames = tools
                .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
                .GroupBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Skip(1).Any())
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            context.AIContext.Tools = tools
                .Select(tool =>
                {
                    var isRestricted = duplicateNames.Contains(tool.Name) || !_allowedToolNames.Contains(tool.Name);
                    return isRestricted
                        ? WrapRestrictedTool(tool, context.Session, wasPlanModeActive: !executeMode)
                        : tool;
                })
                .ToArray();
        }

        return context.AIContext;
    }

    private AIFunction WrapRestrictedTool(AITool tool, AgentSession? session, bool wasPlanModeActive)
    {
        var function =
            tool as AIFunction
            ?? throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                $"Plan mode restrictions for tool '{tool.Name}' require an AIFunction."
            );
        return new PlanModeRestrictedAIFunction(function, _modeProvider, session, _logger, wasPlanModeActive);
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
            return string.Equals(mode, ModeToolBlock.ExecuteModeName, StringComparison.Ordinal);
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
        bool wasPlanModeActive
    )
        : base(innerFunction is PlanModeRestrictedAIFunction existing ? existing.InnerFunction : innerFunction)
    {
        _modeProvider = modeProvider;
        _session = session;
        _logger = logger;
        WasPlanModeActive = wasPlanModeActive;
    }

    internal bool WasPlanModeActive { get; }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (WasPlanModeActive && serviceType == typeof(ApprovalRequiredAIFunction))
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
        var restrictionsApply =
            WasPlanModeActive
            || !await PlanModeToolGuardProvider
                .IsExecuteModeAsync(_modeProvider, _session, _logger, cancellationToken)
                .ConfigureAwait(false);
        if (restrictionsApply)
        {
            var errorCode = ErrorCodes.PlanModeToolNotAllowed.Code;
            return $"{errorCode / 10_000:D3}_{errorCode % 10_000:D4} "
                + $"PlanModeToolNotAllowed: Tool '{Name}' is not available in Plan mode.";
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
