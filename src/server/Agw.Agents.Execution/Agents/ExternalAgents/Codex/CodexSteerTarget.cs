using Agw.Agents.Execution.Steering;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.Protocol;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Codex;

/// <summary>
/// 通过 app-server 的 turn/steer 把输入交给 Codex 正在运行的 turn。
/// 从输出通知得知 thread 与当前 turn；turn/completed 之后、或 Codex 拒绝之后，等到下一个 turn 开始才再次交付。
/// Hands inputs to Codex's running turn through the app-server turn/steer request.
/// The thread and current turn come from output notifications; after turn/completed, or after Codex refuses, delivery waits until the next turn starts.
/// </summary>
internal sealed class CodexSteerTarget : IExternalSteerTarget
{
    private const string TurnCompletedMethod = "turn/completed";

    private readonly CodexAppServerClient _client;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _threadId;
    private string? _turnId;
    private string? _refusedTurnId;

    public CodexSteerTarget(CodexAppServerClient client, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    public void Observe(AgentResponseUpdate update)
    {
        if (update.RawRepresentation is not AppServerNotification { ThreadId: { } threadId } notification)
            return;
        lock (_sync)
        {
            if (string.Equals(notification.Method, TurnCompletedMethod, StringComparison.Ordinal))
            {
                MarkNotReady();
                return;
            }
            if (
                notification.TurnId is not { } turnId
                || string.Equals(turnId, _turnId, StringComparison.Ordinal)
                || string.Equals(turnId, _refusedTurnId, StringComparison.Ordinal)
            )
                return;
            _threadId = threadId;
            _turnId = turnId;
            _ready.TrySetResult();
        }
    }

    public Task WhenReadyAsync(CancellationToken cancellationToken)
    {
        Task ready;
        lock (_sync)
        {
            ready = _ready.Task;
        }
        return ready.WaitAsync(cancellationToken);
    }

    public async Task<ExternalSteerResult> SteerAsync(SteerEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string threadId;
        string turnId;
        lock (_sync)
        {
            if (_threadId is null || _turnId is null)
                return ExternalSteerResult.Retry;
            threadId = _threadId;
            turnId = _turnId;
        }

        try
        {
            await _client
                .TurnSteerAsync(
                    new TurnSteerParams
                    {
                        ThreadId = threadId,
                        ExpectedTurnId = turnId,
                        Input = ToCodexInput(entry),
                        ClientUserMessageId = entry.SteerId.ToString("D"),
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            return ExternalSteerResult.Delivered;
        }
        catch (CodexAppServerException exception)
        {
            // Codex 拒绝时这个 turn 不再接收：turn 已经结束、编号不匹配，或者是 review、compact 这类不可引导的 turn。
            // A refusal means this turn takes no more input: it ended, the ID did not match, or it is a turn kind such as review or compact that cannot be steered.
            _logger.LogInformation(
                exception,
                "Codex refused steering input {SteerId} for turn {CodexTurnId} with code {CodexErrorCode}.",
                entry.SteerId,
                turnId,
                exception.Code
            );
            lock (_sync)
            {
                if (string.Equals(_turnId, turnId, StringComparison.Ordinal))
                {
                    _refusedTurnId = turnId;
                    MarkNotReady();
                }
            }
            return ExternalSteerResult.Retry;
        }
    }

    /// <summary>
    /// 把用户输入转换为 Codex 的输入项：文字与图片，图片使用 data URI 或绝对地址。
    /// Converts the user input into Codex input items: text and images, with images as data URIs or absolute addresses.
    /// </summary>
    internal static IReadOnlyList<UserInput> ToCodexInput(SteerEntry entry)
    {
        var input = new List<UserInput>();
        foreach (var content in SteerDelivery.CreateModelMessage(entry).Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    input.Add(new TextUserInput { Text = text.Text });
                    break;
                case DataContent data when data.HasTopLevelMediaType("image") && !data.Data.IsEmpty:
                    input.Add(new ImageUserInput { Url = data.Uri });
                    break;
                case UriContent uri when uri.HasTopLevelMediaType("image") && uri.Uri.IsAbsoluteUri:
                    input.Add(new ImageUserInput { Url = uri.Uri.AbsoluteUri });
                    break;
                default:
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Codex does not accept '{content.GetType().Name}' content in a steering input."
                    );
            }
        }
        return input;
    }

    /// <summary>
    /// 只在持有锁时调用：当前 turn 不再接收，等待者需要等到下一个 turn。
    /// Called only while holding the lock: the current turn takes no more input, and waiters wait for the next turn.
    /// </summary>
    private void MarkNotReady()
    {
        _turnId = null;
        if (_ready.Task.IsCompleted)
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
