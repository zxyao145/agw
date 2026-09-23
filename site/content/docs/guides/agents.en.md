---
title: "Create a custom agent"
description: "Define an agent with a model, instructions, and the capabilities it needs."
weight: 20
lastmod: 2026-09-23
translationKey: docs/guides/agents
---

An agent is a reusable assistant configuration. Its model interprets requests, its instructions define the task, and its tools determine which operations it can perform. For example, create separate agents for explaining code and reviewing documentation, then select one in Chat.

Start with a working [Model Provider]({{< relref "/docs/guides/providers" >}}). This page covers custom agents run by AGW; see [External agents]({{< relref "/docs/guides/external-agents" >}}) for Claude Code, Codex, and Pi.

## Create an agent

1. Open Agents and create a custom agent with a name that describes its responsibility.
2. Select a Model Provider. Write instructions describing the task, input, and expected output.
3. Select tools, Skills, and configured integration connections as needed. File capabilities require a correct Project workspace.
4. Save, confirm the agent is enabled, and run a small task in Chat.

For example, start with a “Code explainer” that answers questions, then add read-only file capabilities after verifying the model. Instructions cannot grant tool access beyond execution permissions.

![AGW Desktop: an example custom Agent form. Select a Model Provider before saving.](/images/screenshots/agent-create.png)
{caption="AGW Desktop: an example custom Agent form. Select a Model Provider before saving."}

## Give the agent a clear responsibility

Include the task scope and expected output in its instructions. For a documentation reviewer:

```text
Review the documentation I provide for unclear, repetitive, or incomplete passages.
For each issue, show the original text, the problem, and a suggested rewrite.
Preserve facts and limits. Flag uncertain claims instead of inventing features.
```

Test with a short pasted passage. To read project documents directly, add file-reading tools and run it in the correct Project. Instructions describe the task; actual tool configuration and permissions determine access.

## Return replies as JSON

When a program reads the result, paste a JSON Schema object into the Response Schema tab of the create or edit dialog:

```json
{"type":"object","properties":{"summary":{"type":"string"},"issues":{"type":"array","items":{"type":"string"}}},"required":["summary","issues"]}
```

Saving requires valid JSON whose root is an object; an empty value turns structured output off. Anthropic models additionally require `type` set to `object`, `properties` as an object, and `required` as an array. The final reply then follows that structure, and a turn that cannot produce a conforming result fails instead of returning text. With “Generate Turn Summary” enabled, a custom agent reuses that final JSON and does not call the Summary Model Provider.

Among external agents, Claude Code and Codex support this configuration; Pi does not show the tab. See [Structured responses with JSON Schema]({{< relref "/docs/features/structured-output" >}}) for details.

## Edit and reuse

Definition changes take effect on the next turn while retaining the conversation identity. Active turns keep the configuration snapshot captured at their start, including permissions and directories.

Management can copy System Agents; External Agents do not support this copy operation. Check the copied model, capabilities, and project environment before running it.

## Verify

Ask a question matching the agent's responsibility and inspect its tool activity. If tools are missing, check bindings, the tool catalog, and Connection readiness. Increasing permissions does not fix missing configuration.

## Implementation and references

- [Agents module](https://github.com/zxyao145/agw/tree/main/src/server/Agw.Agents)
- [Runtime lifecycle](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
