using System.Runtime.ExceptionServices;
using Agw.Projects.Contracts.Execution;

namespace Agw.Agents.Execution.Agents.Runtime;

/// <summary>
/// 一个 Runtime 的 provider session 绑定状态：绑定组、持久化绑定快照与首次保存失败。创建 Runtime 时的读取、SDK 回调与复用检查使用同一个实例。
/// The provider session binding state of one Runtime: the binding group, the persisted binding snapshot and the first save failure. The read at Runtime creation, SDK callbacks and reuse checks share one instance.
/// </summary>
public sealed class ExternalProviderSessionState
{
    private readonly Lock _sync = new();
    private string? _persistedProviderSessionId;
    private ExceptionDispatchInfo? _failure;

    public ExternalProviderSessionState(ProjectProviderSessionReference reference, string? persistedProviderSessionId)
    {
        Reference = reference;
        _persistedProviderSessionId = persistedProviderSessionId;
    }

    public ProjectProviderSessionReference Reference { get; }

    /// <summary>
    /// Runtime 创建时读取到的生效 ID，或本 Runtime 回调成功保存后的规范化 ID。
    /// The active ID read at Runtime creation, or the normalized ID this Runtime's callback saved successfully.
    /// </summary>
    public string? PersistedProviderSessionId
    {
        get
        {
            lock (_sync)
            {
                return _persistedProviderSessionId;
            }
        }
    }

    public bool HasFailed
    {
        get
        {
            lock (_sync)
            {
                return _failure != null;
            }
        }
    }

    /// <summary>
    /// 绑定保存已经失败时重新抛出首次异常，失败状态持续到 Runtime 释放。
    /// Rethrows the first exception once a binding save has failed; the failure lasts until the Runtime is released.
    /// </summary>
    public void ThrowIfFailed()
    {
        ExceptionDispatchInfo? failure;
        lock (_sync)
        {
            failure = _failure;
        }

        failure?.Throw();
    }

    public void MarkSaved(string providerSessionId)
    {
        lock (_sync)
        {
            _persistedProviderSessionId = providerSessionId;
        }
    }

    public void MarkFailed(Exception exception)
    {
        lock (_sync)
        {
            _failure ??= ExceptionDispatchInfo.Capture(exception);
        }
    }
}
