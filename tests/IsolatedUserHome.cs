namespace Agw.Testing;

/// <summary>
/// 把当前进程的 HOME 临时指向测试输出目录 .scratch 下的隔离目录，展开 <c>~</c> 得到的默认项目目录因此创建在仓库目录内；释放时恢复原值
/// 并删除隔离目录。使用它的测试类属于 <see cref="IsolatedUserHomeCollection"/>，不与其他测试并行执行。
/// Temporarily points the process HOME at an isolated directory under .scratch in the test output directory, so default
/// Project directories expanded from <c>~</c> are created inside the repository; disposal restores the original value
/// and deletes the isolated directory. Test classes using it belong to <see cref="IsolatedUserHomeCollection"/> and do
/// not run in parallel with other tests.
/// </summary>
internal sealed class IsolatedUserHome : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable("HOME");

    public IsolatedUserHome(string name)
    {
        Root = Path.Combine(AppContext.BaseDirectory, ".scratch", name, Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("HOME", Root);
    }

    /// <summary>
    /// 当前平台的用户目录取自 HOME 时为 true；Windows 的用户目录不读取 HOME。
    /// True when the platform takes the user profile from HOME; Windows does not read HOME for it.
    /// </summary>
    public static bool IsSupported => !OperatingSystem.IsWindows();

    public string Root { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        Directory.Delete(Root, recursive: true);
    }
}

[CollectionDefinition(nameof(IsolatedUserHomeCollection), DisableParallelization = true)]
public sealed class IsolatedUserHomeCollection;
