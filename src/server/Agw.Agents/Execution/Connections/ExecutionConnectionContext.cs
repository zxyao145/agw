using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Files.Utils;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Connections;

public sealed class ExecutionConnectionContext : IAsyncDisposable
{
    internal const string BusyMessage =
        "The previous execution is currently in progress, please wait and execute again.";

    private readonly string _userName;
    private readonly IExecutionMessageSink _messageSink;
    private readonly CancellationToken _hostToken;
    private readonly IRuntimeFactory _runtimeFactory;
    private readonly ITaskAppService _taskAppService;
    private readonly IProjectAppService _projectAppService;
    private RuntimeBase? _runtime;
    private TaskProjection? _resolvedTask;
    private string? _workspace;
    private ExecutionTarget? _target;
    private volatile bool _waitingForHuman;

    internal ExecutionConnectionContext(
        string userName,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken,
        IRuntimeFactory runtimeFactory,
        ITaskAppService taskAppService,
        IProjectAppService projectAppService)
    {
        _userName = userName;
        _messageSink = messageSink;
        _hostToken = hostToken;
        _runtimeFactory = runtimeFactory;
        _taskAppService = taskAppService;
        _projectAppService = projectAppService;
    }

    public ExecutionSettings? Settings { get; private set; }

    public string UserName => _userName;

    public Guid? ProjectId => _resolvedTask?.ProjectId ?? Settings?.ProjectId;

    public Guid? ProjectContextId => _resolvedTask?.ProjectContextId;

    public string? ContextId => _resolvedTask?.ContextId ?? Settings?.ContextId;

    public TaskProjection? ResolvedTask => _resolvedTask;

    public string? Workspace => _workspace;

    public ExecutionTarget? Target => _target;

    public Guid? AgentId => _target?.AgentId;

    public AgentRuntimeType? AgentType => _target?.AgentType;

    public bool HasActiveTurn => _runtime is { HasActiveTurn: true };

    public async Task ApplySettingsAsync(
        ExecutionSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (HasActiveTurn)
        {
            await SendErrorAsync(BusyMessage);
            return;
        }

        if (settings.Equals(Settings))
        {
            return;
        }

        await ReleaseRuntimeAsync();
        Settings = settings;
        _resolvedTask = null;
        _workspace = null;
        _target = null;
    }

    public async Task StartTurnAsync(ExecCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (HasActiveTurn)
        {
            await SendErrorAsync(BusyMessage);
            return;
        }

        var agentId = command.AgentId
            ?? throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        if (agentId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        }

        Settings ??= ExecutionSettings.CreateDefault();
        await ResolveExecutionContextAsync(command, cancellationToken);

        var target = new ExecutionTarget(agentId, command.AgentType);
        if (_target.HasValue && _target.Value != target)
        {
            await ReleaseRuntimeAsync();
        }

        var turnContext = new RuntimeTurnContext(
            Settings,
            _resolvedTask!,
            target,
            _userName,
            _workspace!,
            _messageSink,
            pending => _waitingForHuman = pending != null);
        var start = await _runtimeFactory.StartAsync(
            new RuntimeStartRequest(
                target.AgentId,
                _resolvedTask!,
                command,
                _runtime,
                turnContext),
            _hostToken);
        _runtime = start.Runtime;
        _target = start.Runtime == null ? null : target;
        if (start.ActiveTurn == null)
        {
            await SendErrorAsync("Agent execution could not be started.");
        }
    }

    public async Task InterruptTurnAsync(
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!HasActiveTurn)
        {
            await SendSystemMessageAsync(
                reason ?? "No active request is currently running.");
            return;
        }

        _runtime!.RequestInterrupt();
    }

    public async Task SubmitHumanDecisionAsync(
        HumanResponseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_runtime == null
            || !await _runtime.TrySubmitHumanResponseAsync(command, cancellationToken))
        {
            await SendSystemMessageAsync(
                "No matching HumanGate request is waiting for this response.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseRuntimeAsync();
        _resolvedTask = null;
        _workspace = null;
        Settings = null;
    }

    internal bool PrepareForDetach()
    {
        var hasActiveTurn = HasActiveTurn;
        if (hasActiveTurn && _waitingForHuman)
        {
            _runtime!.RequestInterrupt();
        }

        return hasActiveTurn;
    }

    internal Task WhenIdleAsync() => _runtime?.WhenIdleAsync() ?? Task.CompletedTask;

    private async Task ResolveExecutionContextAsync(
        ExecCommand command,
        CancellationToken cancellationToken)
    {
        if (_resolvedTask == null)
        {
            var resolution = await _taskAppService.ResolveTaskAsync(
                new ExecutionTaskRequest(
                    TaskId: null,
                    ProjectId: Settings!.ProjectId,
                    ContextId: Settings.ContextId,
                    Input: AgwMessageUtil.ExtractInputText(command.Input),
                    Resume: Settings.Resume,
                    User: _userName),
                cancellationToken);
            _resolvedTask = resolution.Task
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Execution task could not be resolved.");
        }

        if (_workspace != null)
        {
            return;
        }

        var project = await _projectAppService.GetAsync(_resolvedTask.ProjectId)
            ?? throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Project '{_resolvedTask.ProjectId}' was not found.");
        _workspace = Path.GetFullPath(PathUtil.ExpandTilde(project.GetMustWorkspace().Trim()));
    }

    private async Task ReleaseRuntimeAsync()
    {
        if (_runtime != null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }

        _target = null;
        _waitingForHuman = false;
    }

    private Task SendErrorAsync(string message) =>
        _messageSink.WriteAsync(
            CreateMessage(new AgwErrorContent { Content = message }),
            CancellationToken.None).AsTask();

    private Task SendSystemMessageAsync(string message) =>
        _messageSink.WriteAsync(
            CreateMessage(new AgwTextContent { Content = message }),
            CancellationToken.None).AsTask();

    private static AgwMessage CreateMessage(AgwContent content) =>
        new(
            Guid.CreateVersion7().ToString("D"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [content]);
}
