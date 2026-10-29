using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.ContextualTools.Shell;

/// <summary>
/// 绑定 Project 目录的 shell 执行器。创建内部执行器前以及每次初始化或执行命令前，创建缺失的主目录与附加目录；任一被绑定目录的
/// 物理身份与内部执行器创建时不同（目录被移走后重新创建），释放原 persistent 执行器并重新创建，进程工作目录与挂载随之对应当前目录。
/// A shell executor bound to Project directories. Missing primary and additional directories are created before the
/// inner executor is created and before every initialization or command; when the physical identity of any bound
/// directory differs from the one at inner executor creation (the directory was moved away and recreated), the old
/// persistent executor is disposed and recreated, so the process working directory and mounts follow the current
/// directories.
/// </summary>
internal sealed class ProjectDirectoryShellExecutor : ShellExecutor
{
    private readonly IReadOnlyList<string> _directories;
    private readonly Func<CancellationToken, Task> _validateDirectories;
    private readonly Func<ShellExecutor> _createExecutor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ShellExecutor _executor;
    private DirectoryIdentitySnapshot _boundDirectories;

    public ProjectDirectoryShellExecutor(
        IReadOnlyList<string> directories,
        Func<CancellationToken, Task> validateDirectories,
        Func<ShellExecutor> createExecutor
    )
    {
        _directories = directories;
        _validateDirectories = validateDirectories;
        _createExecutor = createExecutor;
        EnsureDirectoriesCreated();
        _boundDirectories = DirectoryIdentitySnapshot.Capture(_directories);
        _executor = createExecutor();
    }

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executor = await PrepareAsync(cancellationToken).ConfigureAwait(false);
            await executor.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executor = await PrepareAsync(cancellationToken).ConfigureAwait(false);
            return await executor.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 返回调用当前内部执行器函数的 <see cref="AIFunction"/>；名称、说明与参数结构来自内部执行器的
    /// <see cref="ShellExecutor.AsAIFunction"/>。
    /// Returns an <see cref="AIFunction"/> that invokes the current inner executor's function; the name, description
    /// and parameter schema come from the inner executor's <see cref="ShellExecutor.AsAIFunction"/>.
    /// </summary>
    public override AIFunction AsAIFunction(
        string name = "run_shell",
        string? description = null,
        bool requireApproval = true
    ) => new CurrentExecutorFunction(this, name, description, requireApproval);

    public override async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _executor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 创建缺失的被绑定目录；任一目录的物理身份改变时释放原执行器并按当前目录重新创建。调用方持有 <see cref="_gate"/>。
    /// Creates missing bound directories; when any directory's physical identity changed, the old executor is disposed
    /// and recreated over the current directories. The caller holds <see cref="_gate"/>.
    /// </summary>
    private async Task<ShellExecutor> PrepareAsync(CancellationToken cancellationToken)
    {
        await _validateDirectories(cancellationToken).ConfigureAwait(false);
        EnsureDirectoriesCreated();
        if (!_boundDirectories.IsCurrent())
        {
            await _executor.DisposeAsync().ConfigureAwait(false);
            _boundDirectories = DirectoryIdentitySnapshot.Capture(_directories);
            _executor = _createExecutor();
        }

        return _executor;
    }

    private void EnsureDirectoriesCreated()
    {
        foreach (var directory in _directories)
        {
            ProjectWorkspacePaths.EnsureCreated(directory);
        }
    }

    /// <summary>
    /// 每次调用都通过当前内部执行器的函数执行；内部执行器被重新创建后，改用新执行器生成的函数。
    /// Every invocation runs through the current inner executor's function; after the inner executor is recreated, the
    /// function produced by the new executor is used.
    /// </summary>
    private sealed class CurrentExecutorFunction : DelegatingAIFunction
    {
        private readonly ProjectDirectoryShellExecutor _owner;
        private readonly string _name;
        private readonly string? _description;
        private readonly bool _requireApproval;
        private ShellExecutor _functionExecutor;
        private AIFunction _function;

        public CurrentExecutorFunction(
            ProjectDirectoryShellExecutor owner,
            string name,
            string? description,
            bool requireApproval
        )
            : base(owner._executor.AsAIFunction(name, description, requireApproval))
        {
            _owner = owner;
            _name = name;
            _description = description;
            _requireApproval = requireApproval;
            _functionExecutor = owner._executor;
            _function = InnerFunction;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            await _owner._gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var executor = await _owner.PrepareAsync(cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(executor, _functionExecutor))
                {
                    _function = executor.AsAIFunction(_name, _description, _requireApproval);
                    _functionExecutor = executor;
                }

                return await _function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _owner._gate.Release();
            }
        }
    }
}
