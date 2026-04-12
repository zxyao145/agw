# Agw

[中文文档](README.zh-CN.md) | [Documentation](README.md)

Agw 是一个 AssS (Agent as a Service) 平台和 Agent Gateway，可以自定义创建 Agent 和集成外部已有的 Agent（例如 Claude Code、Codex）。

除此之外，Agw 还具备 Job 和 Agent Workflow 能力，可以用于创建定时任务、周期任务、对 Agent 进行编排（目前仅能实现简单的编排）。

本项目主要基于 [MAF](https://github.com/microsoft/agent-framework) 开发。

## 技术栈

Backend:

- .NET 10
- ASP.NET Core
- Entity Framework Core
- Microsoft.Agents.AI
- Serilog + OpenTelemetry

Frontend:

- Next.js 16 App Router
- React 19
- Tailwind CSS 4
- Shadcn 4 （Radix UI）

## 架构

Agw 采用基于领域的模块化单体架构。`src/backend/Agw.Host` 是 ASP.NET Core 程序入口，负责组装各个模块；而前端则位于 `src/frontend/web` 目录下。

典型的后端流程如下：

```text
Controller -> AppService / RuntimeService -> DomainService -> IRepository / IUnitOfWork -> EF Core
```

模块介绍：

```mermaid
flowchart BT
    Agw.Host
    Agw.Infrastructure

    subgraph Core
        direction BT
        Agw.Jobs
        Agw.A2A
        Agw.Agents
        Agw.Providers
        Agw.Skills
        Agw.Tools
        Agw.Integrations
        Agw.Tasks

        %% Relationships
        Agw.Agents --> Agw.Jobs
        Agw.Agents --> Agw.A2A


        Agw.Providers --> Agw.Agents
        Agw.Skills --> Agw.Agents
        Agw.Tools --> Agw.Agents
        Agw.Integrations --> Agw.Agents


        Agw.Tasks --> Agw.Agents
        Agw.Tasks --> Agw.Jobs
        Agw.Tasks --> Agw.A2A

    end

    subgraph Support
        Agw.Setup[Agw.Setup]
    end

    Agw.Shared 

    Agw.Shared --> Core

    Core --> Agw.Infrastructure
    Support --> Agw.Infrastructure

    Agw.Infrastructure --> Agw.Host


    %% styles
    style Core fill:none,stroke:#333,stroke-dasharray: 5 5
    style Support fill:none,stroke:#333,stroke-dasharray: 5 5
```

- Agw.Providers  
  用于管理模型及其供应商。

- Agw.Agents  
  集成外部 Agent（例如 Claude Code、Codex）、管理自定义 Agent。自定义 Agent 可以支持集成 Tool、MCP、Skills。

- Agw.Tools  
  内置 Tool 和 MCP Tool 管理模块。

- Agw.Skills  
  Skill 管理模块。

- Agw.Integrations  
  外部 App 集成模块。

- Agw.Tasks  
  Agent 对话历史与 Session 管理模块。在 Agw 中，一个 Session 对应一个 Task，而每个 Task 都关联一个 Project。

- Agw.Jobs  
  用于提供定时任务、周期任务和一次性任务的能力，支持使用 Cron 表达式。

- 一次性任务：创建后执行一次就会被禁用。

- 定时任务：在指定时间执行，执行一次后被禁用。

- 周期任务：以固定的周期，在指定的时间重复执行。

- Agw.A2A

对外提供 A2A 协议本系统的接口。

## 文档

本项目的详细文档位于： [`docs/`](docs/):

- [Development Guide](docs/1. Development.md): 本地环境配置、构建/测试/代码检查/格式化命令，以及 Git 钩子配置。
- [Architecture](docs/2. Architecture.md): 系统概述、后端/前端架构以及核心领域概念。
- [Module Organization](docs/3. Module Organization.md): 模块内部采用的分层原则。

## 配置

后端主要配置位于 [`src/backend/Agw.Host/appsettings.json`](src/backend/Agw.Host/appsettings.json):

```json
{
  "Database": {
    "Provider": "sqlite",
    "ConnectionString": "Data Source=agw.db"
  },
  "OpenTelemetry": {
    "ServiceName": "Agw",
    "ServiceVersion": "1.0.0",
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

- 数据库 Provider 支持： `sqlite`、`postgres` 和 `MySQL`.
- 请勿将机密信息写入固定配置文件；建议优先使用环境变量进行覆盖。
