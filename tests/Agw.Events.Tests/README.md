# Events 测试

运行 `dotnet test tests/Agw.Events.Tests`，验证 CloudEvents 编解码和两种 transport。
Redis 测试会启动独立的真实 Redis 进程，使用本机空闲端口，并在测试结束时终止进程。
Redis 需要支持 `XAUTOCLAIM`（Redis 6.2 或更新版本）。

默认通过系统 PATH 启动 `redis-server`。
需要指定可执行文件时，在仓库根目录的 `.scratch/event-transport/testsettings.json` 中配置：

```json
{
  "Redis": {
    "ServerExecutable": "/absolute/path/to/redis-server"
  }
}
```

`.scratch/` 已由 `.gitignore` 忽略。
Redis 测试关闭持久化，运行文件保存在 `.scratch/event-transport/` 中。
Redis 启动或连接失败会使测试失败。
