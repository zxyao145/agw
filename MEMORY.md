# Project Memory

## Architecture

Architecture rules are maintained in [AGENTS.md](AGENTS.md) and [docs/rules.md](docs/rules.md); do not duplicate them here. Reusable implementation pitfalls live in [.claude/learning-log.md](.claude/learning-log.md).

## API Design

- Agent 内容消息统一使用 `Append` 语义，新增内容通过唯一输出路径发送；服务器按 MessageId 累积历史正文，执行状态由既有 turn 和 execution 管理。
- Response Schema Result 必须只包含一个 JSON 对象或数组；移除模型添加的 Markdown 围栏和外围说明，无法唯一提取时明确失败。客户端直接展示 JSON，不再包装成 Markdown，避免围栏和小结进入结果。
- For every External Agent, use the canonical AgentName as `AuthorName` on each `AgentResponseUpdate` and persisted `ChatMessage`; never duplicate AgentName in `AdditionalProperties["agentName"]`.
- Store the normalized model identifier only in `AdditionalProperties["modelName"]` on every External Agent update and persisted message; never overload `AuthorName` with a model, and use `""` when neither runtime events nor explicit configuration provide a model.
- Keep External Agent metadata identical across streaming, non-streaming, and history-persistence paths so conversion or replay never changes `AuthorName` or drops `modelName`.

## Tooling

- Never create or merge a pull request automatically. Each remote PR action requires an explicit user request for that action.
- When developing Agw against an adjacent SDK repository, use a local `ProjectReference` instead of publishing or upgrading a temporary NuGet package.
