---
title: "Plan 与 Execute 模式"
description: "先分析方案，再执行操作，把讨论与行动分开。"
weight: 40
lastmod: 2026-09-15
translationKey: docs/features/plan-execute
---

## 先想清楚，再动手

准备修改代码或执行复杂任务时，可以先让 Agent 分析现状和提出方案，再决定是否进入执行阶段。配置 Mode ToolBlock 的自定义 Agent 支持 Plan 与 Execute 两种工作模式。

| 模式 | 适合做什么 | 工具行为 |
| --- | --- | --- |
| Plan | 了解现状、分析问题、整理方案 | 只允许声明可在 Plan 中使用的工具 |
| Execute | 按确认的方案完成工作 | 可使用已配置的工具，仍需遵守权限与审批规则 |

## 一个文档修改的例子

先在 Plan 中要求：“阅读文档，指出术语不清和缺少示例的地方，先给修改建议。”查看方案并确认范围后，再进入 Execute，让 Agent 按方案修改文件。完成后检查差异，确认原有事实和限制被保留。

能否读取或修改文件仍取决于配置的工具。只切换模式，不会自动添加缺少的文件能力。

## 开始使用

1. 为自定义 Agent 配置 Mode ToolBlock 和任务所需工具。
2. 先要求 Agent 在 Plan 模式分析问题，确认当前模式后查看方案。
3. 确认方案后切换到 Execute；Agent 请求切换模式时，在界面中回应确认。
4. 检查实际改动和执行结果，必要时回到 Plan 继续讨论。

```mermaid
flowchart LR
    A["Plan：分析与方案"] --> B["用户确认切换"]
    B --> C["Execute：执行任务"]
    C --> D["检查结果"]
    D --> A
```

Plan 限制由工具的声明和执行检查决定，不能仅靠提示词保证。Execute 也不等于自动批准所有操作：工作模式决定哪些工具能用，审批设置决定调用时是否需要你确认。外部 Agent 使用各自支持的模式与权限能力，不能直接假设与自定义 Agent 完全一致。

[了解工具审批]({{< relref "/docs/features/tool-approval" >}}) · [配置 Tools 与 Skills]({{< relref "/docs/guides/tools-skills" >}})
