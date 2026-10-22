namespace PiAgentSdk;

/// <summary>Configures Pi executable discovery, RPC timeouts, and process environment overrides.</summary>
public sealed record PiAgentOptions
{
    /// <summary>
    /// 指定 Pi CLI 命令或路径，省略时使用 pi；每次启动进程时在子进程环境中查找。
    /// Gets an optional Pi CLI command or path, using pi when omitted; each process start searches the child-process environment.
    /// Unix 上 PATH 未命中时读取该环境 SHELL 指定的 login shell；命中后子进程采用该 shell 的 PATH，相对目录转换为绝对路径。
    /// After a Unix PATH miss, the login shell named by that environment's SHELL is read; on a match the child uses its PATH with relative entries made absolute.
    /// </summary>
    public string? PiPathOverride { get; init; }

    /// <summary>Gets the timeout applied independently to each RPC command. The default is 30 seconds.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the time allowed for abort-and-drain cleanup before the process tree is killed.</summary>
    public TimeSpan AbortGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets explicit environment variables applied after the SDK's sanitized host environment.
    /// </summary>
    /// <remarks>
    /// Host API keys and other arbitrary variables are not inherited unless supplied explicitly.
    /// 查找 Pi CLI 与启动子进程使用同一份环境，SDK 不修改调用方集合。
    /// Pi CLI lookup and the child process use the same environment; the SDK leaves the caller's collection unchanged.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    internal void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout), "Command timeout must be positive.");
        }

        if (AbortGracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AbortGracePeriod), "Abort grace period must be positive.");
        }
    }
}
