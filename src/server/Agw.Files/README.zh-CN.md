# Agw.Files

`Agw.Files` 为 Agw 提供项目文件访问基础设施。HTTP 接口、Agent、运行时和文件工具都通过 `IAgwFileSystem` 访问项目工作区，不直接接受或操作客户端提供的宿主机绝对路径。

## 设计目标

- **统一项目工作区访问**：Agent 和文件工具只依赖 `IAgwFileSystem` 与 `IAgwFileSystemResolver`，不关心文件实际位于本地还是远端。
- **让存储后端可替换**：resolver 按项目选择 Local 或 SFTP，调用方始终使用工作区相对路径。
- **限制路径边界**：所有调用方只传项目相对路径，由具体文件系统实现检查根目录逃逸。
- **集中管理生命周期**：resolver 创建并缓存项目文件系统，释放时统一清理实现了 `IAsyncDisposable` 的远程资源。
- **提供自包含 SDK**：公共接口、实现、Git 能力、路径工具和文件异常都由 `Agw.Files` 提供；使用方直接引用 `Agw.Files`，本模块不依赖 `Agw.Shared`。

当前内置两个项目级后端：

| 后端 | 实现 | 适用场景 |
| --- | --- | --- |
| Local | `LocalFileSystem` | 项目工作区位于 Agw Server 可访问的本地磁盘 |
| SFTP | `SftpFileSystem` | 项目文件位于远程主机，需要通过 SSH/SFTP 访问 |

HTTP 文件操作产生的未处理异常由 `FileEndpointExceptionMappingMiddleware` 转换为边界响应：权限错误返回 `403`，其他异常返回 `500`。

## 模块边界

| 职责 | 所在位置 |
| --- | --- |
| 项目文件的列举、读取、删除、Git diff、重置和文件名搜索 | `Agw.Files.Application.Files.FileAppService` |
| HTTP 参数和响应映射 | `Agw.Files.Api.FilesController`、`Agw.Files.Api.FileEndpointExceptionMappingMiddleware` |
| 项目级 Local/SFTP 文件系统实现与解析 | `Agw.Files.Application.Storage` |
| 文件系统公共契约 | `Agw.Files.Abstracts`、`Agw.Files.Abstracts.Dtos` |
| Git 命令及返回模型 | `Agw.Files.Services` |
| 面向 Agent 的 `read_file`、`write_file`、`ls`、`glob`、`grep` 等工具 | `Agw.Tools.Impl.Files` |
| 项目及其 `Workspace`、`ExtraSetting` 的持久化 | `Agw.Projects` |

`Agw.Files` 不负责定义 Agent 工具，也不拥有项目数据。`Agw.Projects` 通过 `IProjectFileSystemConfigurationProvider` adapter 向 SDK 提供项目配置，Files 不直接依赖 Projects 或 Shared。

## 总体架构

```mermaid
flowchart LR
    Client["HTTP Client"] --> Controller["FilesController"]
    Controller --> AppService["FileAppService"]
    Consumers["Agw.Agents / Agw.Tools"] --> Resolver["IAgwFileSystemResolver"]
    AppService --> Resolver
    Resolver --> Provider["IProjectFileSystemConfigurationProvider"]
    Projects["Agw.Projects adapter"] --> Provider
    Resolver --> Local["LocalFileSystem"]
    Resolver --> Sftp["SftpFileSystem"]
    Local --> LocalStorage["项目本地工作区"]
    Sftp --> RemoteStorage["远程 SFTP 工作区"]
    AppService -. "仅 Local Git 操作" .-> Git["IGitCommandService"]
```

### 项目级文件系统路径

Agent runtime 和 `Agw.Tools` 中的文件工具先调用：

```csharp
var fileSystem = await resolver.ResolveAsync(projectId, cancellationToken);
```

resolver 根据项目配置返回 `LocalFileSystem` 或 `SftpFileSystem`。之后所有路径都应相对于该文件系统的根目录，例如 `README.md`、`src/server/Program.cs`，而不是宿主机绝对路径。

`FilesController` 的路由前缀是 `/api/files`。每个端点接收 `projectId` 和项目相对 `path`；`FileAppService` 使用 resolver 获取对应的 `IAgwFileSystem`。因此切换项目到 SFTP 会同时影响 HTTP 文件浏览和 Agent 文件工具。Git diff、reset 和 `diff=true` 依赖本地仓库，只支持 Local 后端；其他后端返回 `400`。

## 目录结构

- `Application/Files/`：项目文件操作、Git 编排和文件名搜索；
- `Application/Storage/Local/`：本地文件系统及工厂；
- `Application/Storage/Sftp/`：SFTP 文件系统及工厂；
- `Application/Storage/Resolver/`：按项目选择并缓存文件系统；
- `Abstracts/`：项目文件系统公共接口和 DTO；
- `Api/`：HTTP controller、响应 DTO 和异常映射；
- `Exceptions/`：`AgwFilesException` 与 `FilesErrorCode`；
- `Services/`：Git 命令接口、返回模型及实现；
- `Utils/`：文件路径工具；
- `DependencyInjection.cs`：模块服务注册。

调用方通过项目引用直接使用 `Agw.Files.Abstracts` 等 SDK namespace；存储配置类型位于 `Agw.Files` 根 namespace。`Agw.Files.csproj` 没有对 `Agw.Shared` 的项目引用。

## 项目文件系统如何解析

`ProjectScopedFileSystemResolver.ResolveAsync` 按以下顺序工作：

1. `projectId` 为 `Guid.Empty` 时，使用 `~/.agw/temp` 下的临时 Local 文件系统；
2. resolver 已缓存该项目时，直接返回缓存实例；
3. 项目不存在时，记录警告并回退到临时 Local 文件系统；
4. `Project.ExtraSetting` 包含 `fileStorage` 时，按其中的 `type` 创建 Local 或 SFTP 后端；
5. 没有 `fileStorage` 配置时，使用 `Project.Workspace`；如果 Workspace 为空，则使用 `~/.agw/{project.Name}`。

resolver 以 singleton 注册，并按 `projectId` 缓存文件系统。当前没有缓存失效机制：进程运行期间修改项目的 `Workspace` 或 `ExtraSetting.fileStorage`，已经解析过的项目不会自动切换后端。需要配置即时生效时，应先设计明确的缓存失效和旧连接释放机制。

resolver 自身不查询 Agw 项目。它在独立 DI scope 中调用 `IProjectFileSystemConfigurationProvider`；Agw Host 由 `Agw.Projects` 注册 `ProjectFileSystemConfigurationProvider` adapter。其他使用 SDK 的 Host 可以提供自己的 adapter。

## 使用方式

### 注册模块

Host 通过 `AddFiles` 注册 `FileAppService`、Git 命令、存储工厂、默认 resolver 和默认 `TimeProvider.System`：

```csharp
builder.Services.AddFiles(builder.Configuration);
```

`Agw.Host/Program.cs` 还注册了 `FileEndpointExceptionMappingMiddleware`。按项目解析文件系统时，Host 还必须注册 `IProjectFileSystemConfigurationProvider`；Agw Server 通过 `AddProjects` 完成这项注册。如果在其他 Host 中复用本模块，HTTP 文件接口需要同时接入 middleware，项目级 `IAgwFileSystem` 本身则不依赖它。

`TimeProvider` 使用 `TryAddSingleton` 注册，因此测试或其他 Host 可以在调用 `AddFiles` 前注册自己的时间源。

### 配置 Local 存储

下面的 JSON 是 `Project.ExtraSetting` 的内容，不是独立的 `appsettings.json` 配置节：

```json
{
  "fileStorage": {
    "type": "local",
    "local": {
      "rootPath": "/srv/agw/workspaces/demo"
    }
  }
}
```

显式 `fileStorage.local.rootPath` 建议使用绝对路径。当前从显式 Local 配置创建文件系统时不会展开 `~`；没有 `fileStorage` 配置、回退到 `Project.Workspace` 时才会经过支持 `~` 展开的工厂重载。

### 配置 SFTP 存储

私钥认证示例：

```json
{
  "fileStorage": {
    "type": "sftp",
    "sftp": {
      "host": "sftp.example.com",
      "port": 22,
      "username": "agw",
      "authType": "privateKey",
      "privateKeyPath": "/home/agw/.ssh/id_ed25519",
      "rootPath": "/srv/projects/demo"
    }
  }
}
```

`privateKeyPath` 是 Agw Server 所在机器上的路径，不是远程路径；加密私钥还可以提供 `passphrase`。密码认证则把 `authType` 设为 `password` 并提供 `password`。当前实现直接从 `Project.ExtraSetting` 读取这些值，不要把真实凭据提交到仓库、示例配置或日志中。

### 在应用服务中访问项目文件

调用方注入 `IAgwFileSystemResolver`，解析项目后再使用相对路径：

```csharp
using Agw.Files.Abstracts;

public sealed class WorkspaceDocumentService
{
    private readonly IAgwFileSystemResolver _fileSystemResolver;

    public WorkspaceDocumentService(IAgwFileSystemResolver fileSystemResolver)
    {
        _fileSystemResolver = fileSystemResolver;
    }

    public async Task<string> ReadReadmeAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var fileSystem = await _fileSystemResolver.ResolveAsync(projectId, cancellationToken);
        return await fileSystem.ReadAllTextAsync("README.md", cancellationToken);
    }
}
```

`IAgwFileSystem` 提供以下能力：

| 方法 | 作用 |
| --- | --- |
| `ExistsFileAsync` / `ExistsDirectoryAsync` | 判断文件或目录是否存在 |
| `StatAsync` | 获取相对路径、类型、大小和 UTC 修改时间 |
| `ReadAllTextAsync` / `ReadAllLinesAsync` | 读取文本文件 |
| `WriteAllTextAsync` | 创建或覆盖文本文件 |
| `CreateDirectoryAsync` | 创建目录 |
| `DeleteAsync` | 删除文件或递归删除目录 |
| `EnumerateAsync` | 按 glob 风格的 `searchPattern` 枚举目录 |
| `SearchAsync` | 按正则表达式搜索文件内容，返回文件、行号和文本 |

所有 I/O 方法都接收 `CancellationToken`。新增调用方或实现时，不要用 `CancellationToken.None` 覆盖上游取消信号。

### 使用 HTTP 文件接口

HTTP 接口要求非空 `projectId`，`path` 使用项目相对路径；列举和搜索时省略 `path` 表示项目根目录，读取、diff、删除和 reset 则要求非空 `path`。接口受 Host 认证边界保护：

| 方法与路由 | 主要参数 | 作用 |
| --- | --- | --- |
| `GET /api/files/list` | `projectId`、`path`、`diff`、`recursive` | 列出直接子项；Local 后端可以按 Git 变更过滤 |
| `GET /api/files/read` | `projectId`、`path` | 读取文本文件 |
| `GET /api/files/diff` | `projectId`、`path` | 获取 Local 项目中单个文件的 Git diff |
| `DELETE /api/files/delete` | `projectId`、`path` | 删除文件或递归删除目录；不允许删除项目根目录 |
| `POST /api/files/reset` | `projectId`、`path` | 把 Local 项目中的文件重置到 Git HEAD |
| `GET /api/files/search` | `projectId`、`path`、`keyword`、`limit`、`recursive` | 按相对路径名称搜索文件和目录 |

例如，列出一个目录中的 Git 变更：

```bash
curl --get 'http://localhost:5015/api/files/list' \
  --header 'Authorization: Bearer agw_...' \
  --data-urlencode 'projectId=11111111-1111-1111-1111-111111111111' \
  --data-urlencode 'path=src' \
  --data-urlencode 'diff=true'
```

这里有两个同名但语义不同的“搜索”：`GET /api/files/search` 按文件或目录的相对路径匹配 `keyword`；`IAgwFileSystem.SearchAsync` 则读取文件内容并按正则表达式返回命中行。

## 路径与安全约束

传给 `IAgwFileSystem` 的路径必须相对于项目根目录。Local 和 SFTP 实现都会拒绝绝对路径及包含 `..` 的逃逸路径；新的远程或对象存储实现也必须把 root、bucket prefix 或 tenant prefix 当作安全边界，而不只是字符串拼接前缀。

Local 的检查是规范化后的词法路径检查，不解析符号链接的最终目标。部署时不要在项目根目录中放置指向敏感目录的符号链接；如果要增强这一点，需要同时考虑跨平台链接解析、目标不存在和竞态条件。

`DELETE /api/files/delete` 会递归删除目录，但空路径会返回 `400`，防止删除整个项目文件系统根目录。

对所有后端，建议保持这些语义一致：

- 返回给调用方的是项目相对路径；
- `FileEntry.LastModifiedUtc` 使用 `DateTimeOffset` 和 UTC；
- 不存在的路径由 `Exists*Async` 或 `StatAsync` 表达，不引入后端特有的返回格式；
- SDK 的预期错误使用 `AgwFilesException` 及 `FilesErrorCode`；异常同时公开稳定的七位数字 `Code` 和 `StatusCode`，方便 Host 在 HTTP、SignalR 等协议边界完成转换；
- 远程连接和流必须在取消或释放时正确清理。

## 怎么扩展

### 新增存储后端

以新增一种 `WebDav` 后端为例，最小改动面如下。

#### 1. 扩展公共配置契约

在 `Agw.Files` 根 namespace 中增加后端所需的 options；如果调用方需要枚举类型，同时扩展 `FileStorageType`。第三方 SDK、HTTP client 等实现细节仍留在 `Agw.Files` 的实现目录，不进入公共契约。

配置字段应只描述连接和根目录，例如 endpoint、用户名、凭据引用和 root path。需要特别说明哪些路径属于 Agw Server，哪些路径属于远端。

#### 2. 实现 `IAgwFileSystem`

在 `Agw.Files/Application/Storage/WebDav/` 中增加 `WebDavFileSystem`，完整实现 `IAgwFileSystem` 的每个成员。实现时重点检查：

- 路径规范化和根目录逃逸；
- 相对路径返回值在不同后端间是否一致；
- glob、内容搜索、大小和修改时间的语义；
- `CancellationToken` 是否传递到网络和流操作；
- 并发访问时客户端是否线程安全；
- 实现 `IAsyncDisposable` 后，是否能关闭连接并释放流。

如果后端基于 HTTP，必须通过 `IHttpClientFactory` 创建客户端，不要直接 `new HttpClient()`。

#### 3. 增加工厂

工厂负责验证 options、构造 SDK client，并把后端根目录传给文件系统实现。配置缺失、认证类型不支持等预期错误应抛出带 `FilesErrorCode` 的 `AgwFilesException`。

工厂不应读取项目或缓存实例；这些职责仍属于 `ProjectScopedFileSystemResolver`。

#### 4. 接入 resolver

在 `ProjectScopedFileSystemResolver` 中完成三处接入：

1. 通过构造函数注入新工厂；
2. 在 `CreateFromConfig` 的 `type` 分支中识别新类型；
3. 增加一个私有解析方法，把 JSON 配置映射到 options 并调用工厂。

当前 resolver 使用小写字符串选择后端，因此配置值应大小写不敏感，并为未知值返回 `FilesErrorCode.UnsupportedStorageBackend`。

#### 5. 注册依赖

在 `Agw.Files/DependencyInjection.cs` 中注册新工厂。默认 `IAgwFileSystemResolver` 仍保持 singleton；如果新 client 的生命周期不适合被项目级长期缓存，应先调整缓存设计，而不是在文件系统方法内部反复创建连接。

#### 6. 补齐验证

至少覆盖以下测试：

- 必填配置缺失时返回稳定的 `FilesErrorCode`；
- `..`、绝对路径和根目录前缀碰撞不能逃逸存储根；
- 读、写、覆盖、枚举、搜索和递归删除的行为与现有契约一致；
- 取消信号可以终止较慢的 I/O；
- resolver 能从 `Project.ExtraSetting.fileStorage` 选择新后端；
- resolver 释放时会释放已缓存的异步资源。

远程后端测试优先针对可替换的 client adapter 编写单元测试，再用少量集成测试验证真实服务器协议，避免把所有测试都绑定到外部服务。

### 扩展项目解析策略

如果选择后端不再只依赖 `Project.ExtraSetting`，应扩展或替换 `IAgwFileSystemResolver`，而不是让 Agent 工具分别读取项目配置。resolver 必须继续提供单一选择结果，并明确：

- 配置优先级；
- 项目不存在时是否允许回退；
- 缓存键和失效条件；
- 旧文件系统何时释放；
- 配置切换期间正在执行的 turn 如何处理。

这类改动会影响 `Agw.Agents` 和 `Agw.Tools` 的运行时行为，不能只按普通工厂扩展处理。

### 扩展 HTTP 文件接口

新增 `/api/files/*` 端点时：

1. 接收 `projectId` 和项目相对路径，通过 `FileAppService` 与 `IAgwFileSystemResolver` 访问项目存储；
2. 把文件 I/O、Git 编排和操作日志实现放入 `FileAppService`，controller 只映射 `FileOperationResult<T>`；
3. 在 `FileEndpointExceptionMappingMiddleware` 中补充操作名和失败消息；
4. 保持认证、相对路径限制和日志信息；
5. 按 `docs/rules.md` 检查 API 响应与异常约束，不要只复制旧端点的返回方式；
6. 在 `Agw.Files.Tests` 中分别覆盖 application 操作行为和 HTTP adapter 映射。

不要重新引入未经约束的宿主机绝对路径参数；需要后端特有能力时，应显式定义能力边界和不支持时的响应。

## 测试

从仓库根目录运行：

```bash
dotnet test tests/Agw.Files.Tests/Agw.Files.Tests.csproj
```

只验证模块能否编译时，可以运行：

```bash
dotnet build src/server/Agw.Files/Agw.Files.csproj
```

现有测试覆盖 `FileAppService` 的文件、Git 和文件名搜索行为，以及相对路径安全、异常映射和 controller 的模块归属。修改存储实现或 resolver 时，应另外补充对应后端和项目配置解析测试。

## 常见误区

- **给项目文件系统传绝对路径**：`IAgwFileSystem` 的调用方应使用相对于工作区根目录的路径。
- **在 SFTP 项目中请求 Git 操作**：Git diff、reset 和变更过滤只支持 Local 项目文件系统。
- **在显式 Local 配置中依赖 `~` 展开**：当前显式 `fileStorage.local.rootPath` 路径不会经过展开 `~` 的工厂重载，建议使用绝对路径。
- **修改项目配置后期待缓存立即刷新**：resolver 会缓存已经解析的项目文件系统，当前没有主动失效机制。
- **混淆两种搜索**：HTTP `search` 搜索路径名称，`IAgwFileSystem.SearchAsync` 搜索文件内容。
- **忽略远程资源释放**：远程实现应实现 `IAsyncDisposable`，并确保 resolver 能释放缓存实例。
- **把真实 SFTP 凭据放进示例或版本控制**：当前配置支持直接读取密码和 passphrase，维护者需要自行保证配置数据的访问边界。

简单来说，HTTP、Agent 和文件工具共享同一条项目文件系统边界。新增存储后端通常只需要扩展公共配置、具体实现、工厂、resolver、DI 和对应测试；只有 Git 等后端特有能力需要额外定义支持范围。
