---
title: "测试与贡献约定"
description: "按影响范围验证改动，遵守格式、边界和迁移规则。"
weight: 50
lastmod: 2026-11-26
translationKey: docs/development/testing
---

前提：依赖已安装。修改前阅读根目录 `AGENTS.md` 和 `docs/human/` 中的相关规则，保留无关的本地改动。

## 后端检查

从仓库根运行：

```bash
dotnet build Agw.slnx
dotnet test Agw.slnx
dotnet csharpier check .
```

测试项目使用 xUnit v3，并通过根目录 `global.json` 使用 Microsoft.Testing.Platform 运行。定位问题时先运行相关测试，例如 `dotnet test tests/Agw.Files.Tests`。单元和组合测试使用真实实现和纯选项辅助方法，不使用 mock 或 fake 实现。运行后端 Agent 测试前，先安装固定版本的 Codex app-server 测试夹具（`npm ci --prefix tests/Agw.Agents.Tests/fixtures/codex-app-server --ignore-scripts --no-audit --no-fund`）。构造 `CodexAIAgent` 或 `ClaudeCodeAIAgent` 时会探测 CLI，真实 CLI 测试需要显式启用并确认可执行文件可用，不放进默认测试套件；ACP Agent 测试可执行 `dotnet test --project tests/Agw.Agents.Tests --filter-class "*Acp*"`。

Server 扩展相关测试位于 `tests/Agw.Extensions.Tests`、`tests/Agw.Extensions.Runtime.Tests`、`tests/Agw.Extensions.Database.Tests` 与 `tests/Agw.Host.Tests`（`ExtensionStartupTests`）。

修改错误码或异常规则后运行 `dotnet test tests/Agw.Shared.Tests`；修改模块依赖后运行后端架构测试 `dotnet test tests/Agw.Architecture.Tests`。

持久化执行的 PostgreSQL 测试（租约保护、事件顺序、活动执行升级和调度容量）通过 `AGW_TEST_POSTGRES_CONNECTION_STRING` 连接独立的测试实例，测试用户需要创建数据库的权限。Jobs、Files、Events 与扩展的 PostgreSQL 测试从 `.scratch/postgres-tests.json` 读取 `ConnectionString`，Redis 相关测试从 `.scratch/redis-tests.json`（或 `AGW_TEST_REDIS_CONNECTION_STRING`）读取配置；未配置时对应测试会跳过。CI 使用 PostgreSQL 18 运行 PostgreSQL 测试，并检查 TRX 结果，确认必需的测试全部执行成功；完整命令见[开发文档](https://github.com/zxyao145/agw/blob/main/docs/1.Development.md)。

改动登录相关代码时，运行 `dotnet test tests/Agw.Auth.Tests`。这些测试默认使用受控的提供商和各自独立的 SQLite 数据库。需要在 PostgreSQL 上验证时，把 `AGW_TEST_OIDC_POSTGRES` 设为测试服务器的管理连接字符串，该账号需要能够创建数据库；不要指向生产服务器。Desktop 主进程的登录与凭据存储测试使用 `pnpm --filter @agw/desktop test`。

## 客户端检查

从 `src` 运行：

```bash
pnpm lint
pnpm test
pnpm fmt:check
pnpm build
```

使用 oxlint/oxfmt，不使用 ESLint/Prettier。修改包边界后必须通过 `pnpm test:boundaries`（已包含在 `pnpm test` 中）；改变 API 后先把 Development 环境的 OpenAPI 文档导出到 `src/clients/packages/api/openapi.json`，再运行 `pnpm gen:api` 重新生成 typed client，并验证调用方。

组件渲染测试通过共享的 `@agw/test-harness` 建立 DOM 环境；需要 API 响应时，用其中的 `startApiServer` 启动真实的本地 HTTP 服务，让组件走自身的请求路径。浏览器与 Electron 检查包括：

- Web 浏览器端到端测试（`test:e2e`、`test:e2e:export`、`test:e2e:extensions`）：先在仓库根构建 `Agw.slnx` 或 `src/server/Agw.Standalone.Host`，安装 Chromium（`pnpm --filter @agw/web exec playwright install chromium`），再从 `src/clients` 运行对应脚本。
- Code view 与 Files 浏览器测试：`pnpm --filter @agw/code-view test:browser` 与 `pnpm --filter @agw/projects test:browser`。
- Desktop 扩展集成测试：`pnpm --filter @agw/desktop test:extensions`（每次运行前需经过用户确认）。

## 数据与提交

模型变更需要配套 SQLite 与 PostgreSQL 迁移，但只有明确授权后才生成或应用。生成时分别以 `src/server/Agw.Migrations.Sqlite` 和 `src/server/Agw.Migrations.Postgres` 为迁移项目、`src/server/Agw.Standalone.Host` 为启动项目，并在命令末尾传入 `-- --provider sqlite` 或 `-- --provider postgres`，完整命令见开发文档。`dotnet tool restore` 只安装 CSharpier，`dotnet ef` 需要另行安装。`NoForeignKeyModelDiffer` 禁止生成数据库外键，引用验证和清理由应用层/基础设施负责。

C# 使用显式构造函数，禁止 primary constructor；日期使用 `DateTimeOffset`。遵守根目录 `AGENTS.md`。提交需显式授权，并使用 Conventional Commits。

## 按改动选择检查

| 改动 | 至少确认 |
| --- | --- |
| 修复后端行为 | 相关项目构建通过，能重现原问题的验证通过 |
| 修改接口或 DTO | 导出 OpenAPI 文档并重新生成 API 类型，检查调用方及错误处理 |
| 调整模块或包依赖 | 后端架构测试或客户端边界检查通过 |
| 修改错误码 | `tests/Agw.Shared.Tests` 通过 |
| 修改界面 | 检查实际操作、不同屏幕宽度和相关测试；涉及 Web 浏览器行为时运行 `test:e2e` |
| 修改本站文档 | Hugo 严格构建、链接与中英文对应检查通过，页面显示正常 |

验证失败时保留错误信息，修复后重跑受影响的检查。提交说明应写清改了什么、如何验证，以及是否影响数据库或部署。

## 完成标准

缺陷修复应有可复现验证，行为改动覆盖相关成功和失败路径。文档站只改 `site` 时运行其 Hugo、链接和浏览器检查，无需为纯文档改动启动模型服务或执行数据库初始化。

## 实现与参考

- [Repository rules](https://github.com/zxyao145/agw/blob/main/AGENTS.md)
- [Development](https://github.com/zxyao145/agw/blob/main/docs/1.Development.md)
