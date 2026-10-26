using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Coordination;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Testing;

/// <summary>
/// 测试中构造本地文件系统：所有实例共用一组使用进程内应用锁的 FilePathLocks，日志不输出。
/// Builds local file systems for tests: every instance shares one FilePathLocks over the in-process application lock,
/// and logs are discarded.
/// </summary>
internal static class TestFileSystems
{
    public static FilePathLocks PathLocks { get; } = new(InMemoryApplicationLock.Shared);

    public static LocalFileSystem Local(string rootPath) => new(rootPath, PathLocks, NullLogger.Instance);
}
