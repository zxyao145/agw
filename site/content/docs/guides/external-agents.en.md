---
title: "Connect external agents"
description: "Run tasks with Claude Code, Codex, Pi, ACP, or remote A2A agents and configure separate agents for different purposes."
weight: 30
lastmod: 2026-11-26
translationKey: docs/guides/external-agents
---

When you connect an external agent, tools such as Claude Code, Codex, Pi, an ACP agent, or a remote A2A agent perform the actual work. AGW provides a shared interface for configuration, conversations, and workflows, so you can keep using familiar tools while managing how you use them in AGW.

Prerequisites: for CLI-based agents, the matching CLI is installed on the execution node and works under the Server's account and environment. Installing it on a browser machine is insufficient; container execution needs the CLI inside the container. For remote A2A agents, the execution host must be able to reach the remote Agent Card URL and service endpoint.

ACP agents can be installed from the registry or launched with a command available to the Server. Select the installed agent in **Extra Settings**, or configure `command` and optional `args`. See [ACP installation and deployment](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md).

## One external agent type, separate configurations

You can create multiple AGW Agent definitions that use the same external agent type, each with its own model selection and settings. For example, both of these agents run Claude Code, but serve different purposes:

| Agent in AGW | External runtime | Model | Purpose |
| --- | --- | --- | --- |
| Coding | Claude Code | model1 | Write and modify code |
| Review | Claude Code | model2 | Review code and suggest improvements |

For both definitions, select **External → Claude Code**, then choose a Model Provider pointing to `model1` or `model2`, respectively. These model names are examples; replace them with models available from your service provider and compatible with the Anthropic protocol.

Selecting Coding or Review in Chat uses that definition’s model configuration. You can also use them in different Agentflow nodes, without repeatedly editing a single Agent definition to switch purposes.

This separation applies to the settings stored in each AGW Agent definition. It does not automatically create separate operating-system accounts or file environments. If a definition has no Model Provider selected, the model comes from the agent's **Extra Settings** or the external tool’s own model configuration.

## Configure

1. Verify the CLI on the execution node (or confirm the remote A2A Agent Card URL is reachable) and complete its authentication or model configuration.
2. In **Agents**, click **Create**, set **Agent Type** to `External`, and choose the external agent kind. The kind cannot be changed after creation.
3. Select a Project and ensure its primary workspace is visible to the execution process.
4. Configure target-specific fields:
   - **Claude Code, Codex, or Pi**: optionally select a compatible Model Provider; leave it empty to use **Extra Settings** or the external tool's own configuration.
   - **ACP**: set model, authentication, and mode with `configOptions`, `authMethodId`, and `modeId` in **Extra Settings**.
   - **A2A**: enter the **Agent Card URL** (for example `https://example.com/.well-known/agent-card.json`), add any required origin-bound HTTP header credentials, and click **Fetch card** (or **Refresh card** when editing) to resolve the remote interface.
5. Send a short task and verify the working directory, output, and permission mode.

Claude Code and Codex receive the Project's additional directories through their SDK directory options; Pi receives the directory list in each turn's context. These three start in the primary workspace.

| External agent | Model / endpoint configuration | Permissions |
| --- | --- | --- |
| Claude Code | Optional Anthropic Model Provider | Uses the capabilities declared for this target |
| Codex | Optional OpenAI Responses Model Provider | Currently FullAccess only |
| Pi | Optional Model Provider (all three OpenAI/Anthropic protocols) | Currently FullAccess only |
| ACP | Configure model and authentication through the ACP agent | InProcess supports all three modes; Distributed supports FullAccess only |
| A2A | Remote Agent Card URL and origin-bound header credentials | FullAccess only (remote agents run their own tools) |

For ACP registry agents, install the selected version on each execution host. In Distributed mode, share the ACP agent's session storage across Data Plane replicas.

## Use an ACP external agent

ACP (Agent Client Protocol) runs external agents over stdio JSON-RPC. AGW supports any ACP-compatible program (such as Gemini CLI, OpenCode, Goose, or Codex ACP), launched either from the official ACP registry or through a custom command on the execution host.

### Option 1: select from the ACP Registry

1. Install the agent on each execution host (`Standalone` or `Data Plane`) under the same operating-system account and `AgwDataDir` (or `AcpAgents:InstallRoot`) used by the running Server:

```bash
./agw-server acp install goose codex-acp
```

For container deployments, install registry agents while building a derived execution image; see [Server extensions and ACP hosts]({{< relref "/docs/operations/extensions" >}}). The Server never downloads agent binaries at runtime.

2. In **Agents → Create**, set **Agent Type** to `External` and choose `ACP`.
3. Search and pick the installed agent in the **ACP Registry** selector (click **Refresh** to reload the catalog). Selecting an entry writes `registry` into **Extra Settings** and removes `command` / `args`. If **Extra Settings** contains invalid JSON, fix it before using the selector.

### Option 2: launch a custom command

When an ACP-compatible CLI is already installed on the host, skip the registry and set `command` and optional `args` in **Extra Settings**:

```json
{
  "command": "gemini",
  "args": ["--acp"],
  "configOptions": {
    "model": "gemini-2.5-pro"
  }
}
```

### ACP Extra Settings fields

| Key | Type | Description |
| --- | --- | --- |
| `registry` | object, optional | `{ "agentId": "<registry id>", "version": "<major.minor.patch>" }`. When `version` is omitted, AGW selects the highest installed version in the install root |
| `command` | string, optional | Executable name or path for a custom command |
| `args` | string[], optional | Command arguments; valid only together with `command` |
| `authMethodId` | string, optional | Authentication method ID passed to `authenticate` after initialization |
| `modeId` | string, optional | Target mode ID applied after session creation |
| `configOptions` | object, optional | Key-value map applied after session creation; values are strings or booleans (for example selecting a model) |

Provide either `registry` or `command`, never both. Example using a registry entry with `configOptions`:

```json
{
  "registry": { "agentId": "goose" },
  "configOptions": {
    "model": "claude-sonnet-4-5"
  }
}
```

### Session continuity, credentials, and replicas

- **Session continuity**: AGW stores and reuses the ACP `sessionId` across turns in the same conversation, restoring it with `session/resume` or `session/load` after process restarts, runtime rebuilds, or switching Data Plane replicas.
- **Credentials and environment**: ACP processes run under the Server's operating-system account. Supply API keys or state paths (such as `CODEX_HOME`) through Agent or Project environment variables, or sign in to the CLI under the Server account.
- **Permissions and Distributed mode**: InProcess supports `AlwaysAsk`, `AllowSameArguments`, and `FullAccess`, routing ACP permission requests through AGW's approval flow. Distributed mode supports `FullAccess` only and requires identical agent versions plus shared session storage mounted at the same path across all Data Plane replicas.

## Changes and limitations

The Chat permission menu always lists all three modes and disables those the target does not support, with the reason shown; the server also validates them. ACP supports all three modes in InProcess and FullAccess in Distributed. Permission or definition edits affect the next turn. Active turns keep the configuration snapshot captured at their start.

Claude Code, Codex, and Pi require InProcess execution when run directly in Chat. ACP and remote A2A agents support both InProcess and Distributed execution; Distributed ACP requires FullAccess and shared ACP session storage across replicas. See the [split deployment guide]({{< relref "/docs/operations/split" >}}) and [ACP deployment configuration](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md).

AGW's Instructions, Tools, Skills, MCP Tool Server, and Integrations settings are not passed to any External Agent, including Pi, ACP, and A2A, and those tabs cannot be edited in the form. External agents only receive existing User Memory as context. Configure and verify the external tool's capabilities in its own environment.

If the CLI is unavailable, check its executable, account, environment variables, and Server logs. If a CLI works in your terminal but fails in AGW, check that the Server account’s PATH includes the executable.

## Implementation and references

- [External agent runtime](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
- [Integration limits](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
