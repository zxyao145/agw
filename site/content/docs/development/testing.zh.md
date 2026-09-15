---
title: "测试与贡献约定"
description: "按影响范围验证改动，遵守格式、边界和迁移规则。"
weight: 50
lastmod: 2026-09-15
translationKey: docs/development/testing
---

前提：依赖已安装。修改前阅读根目录 AGENTS.md 和 `docs/rules.md`，保留无关的本地改动。

## 后端检查

从仓库根运行：

```bash
dotnet build Agw.slnx
dotnet test Agw.slnx
dotnet csharpier check .
```

定位问题时先运行相关测试，例如 `dotnet test tests/Agw.Files.Tests`。使用 fake `AIAgent`，不要在默认单元或组合测试中创建会探测 CLI 的 Codex/Claude Agent。真实 CLI 测试需明确启用并检查可执行文件。

## 客户端检查

从 `src/clients` 运行：

```bash
pnpm lint
pnpm test
pnpm fmt:check
pnpm build
```

使用 oxlint/oxfmt，不是 ESLint/Prettier。修改包边界后必须通过 `pnpm test:boundaries`；改变 API 后重新生成 typed client 并验证调用方。

## 数据与提交

模型变更需要配套 SQLite 与 PostgreSQL 迁移，但只有明确授权后才生成或应用。`NoForeignKeyModelDiffer` 禁止生成数据库外键，引用验证和清理由应用层/基础设施负责。

C# 使用显式构造函数，禁止 primary constructor；日期使用 `DateTimeOffset`。AGENTS.md 与 CLAUDE.md 保持相同。提交需显式授权，并使用 Conventional Commits。

## 按改动选择检查

| 改动 | 至少确认 |
| --- | --- |
| 修复后端行为 | 相关项目构建通过，能重现原问题的验证通过 |
| 修改接口或 DTO | 重新生成 API 类型，检查调用方及错误处理 |
| 调整模块或包依赖 | 后端架构测试或客户端边界检查通过 |
| 修改界面 | 检查实际操作、不同屏幕宽度和相关测试 |
| 修改本站文档 | Hugo 严格构建、链接与中英文对应检查通过，页面显示正常 |

验证失败时保留错误信息，修复后重跑受影响的检查。提交说明应写清改了什么、如何验证，以及是否影响数据库或部署。

## 完成标准

缺陷修复应有可复现验证，行为改动覆盖相关成功和失败路径。文档站只改 `site` 时运行其 Hugo、链接和浏览器检查，无需为纯文档改动启动模型服务或执行数据库初始化。

## 实现与参考

- [Repository rules](https://github.com/zxyao145/agw/blob/main/AGENTS.md)
- [Development](https://github.com/zxyao145/agw/blob/main/docs/1.Development.md)
