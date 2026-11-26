---
title: "Server extensions and ACP hosts"
description: "Install Server extensions, configure ACP registry agents, and manage host runtime dependencies."
weight: 35
lastmod: 2026-11-26
translationKey: docs/operations/extensions
---

Server extensions add server-side APIs, event handlers, notification channels, Skills, and published Desktop views to AGW Hosts. ACP registry agents install external agent binaries or runtimes on execution Hosts. Both require preparation on the Host filesystem and restart coordination.

## Server extensions

Install, update, and remove Server extensions while **every** participating Host (`Standalone`, `Control Plane`, and `Data Plane`) is stopped.

Each extension lives in its own immediate subdirectory under `Extensions:Directory` (default `./Extensions`, relative to the Server executable directory) with `manifest.json` at its root and every declared file present. In split deployments, give every Host the same extension directory contents.

```json
{
  "Extensions": {
    "Directory": "./Extensions",
    "PublicBaseUrl": "https://agw.example.com"
  }
}
```

### Enable and manage extensions

1. Stop all Hosts, copy or update the extension folder under `Extensions:Directory`, and start the Hosts. Each Host scans the directory once at startup and builds an immutable local snapshot; missing directories start with an empty catalog, and invalid manifests or file digests fail startup.
2. Sign in to Web as an administrator and open **System → Server extensions** (`/extensions`) to inspect discovered versions and the current Host's runtime state.
3. Enable the extension. New extensions start disabled. Enablement is stored globally in the `setting` table (`extensions.enabled.<id>`) and applies across Hosts, while runtime state and errors remain local to each Host.

Disabled or removed extension directories retain their enablement settings, extension business tables, migration history, and data directories. Enabled extensions provide their registered Skills automatically during capability composition without importing them on the Skills page.

### Workspace extensions (TaskPilot and Feishu)

The repository provides two workspace extensions under `src/extensions/`:

- **TaskPilot** (`@agw/extension-taskpilot`): turns subscribed events into user-owned matters and rounds, manages GitHub pull request deliveries using the user's GitHub Connection, and exposes a Desktop view. Build from `src` with `pnpm --filter @agw/extension-taskpilot build`.
- **Feishu** (`@agw/extension-feishu`): connects Feishu notifications and identity binding. Build from `src` with `pnpm --filter @agw/extension-feishu build`. Hosts running Feishu require Node.js available on `PATH` as `node` for its SDK worker, and require `Extensions:PublicBaseUrl` to be set to the browser-accessible HTTPS base URL (loopback HTTP is accepted in local development).

## ACP registry agents

An ACP Agent runs on the execution Host (`Standalone` or `Data Plane`). Install registry agents on the same operating-system account and `AgwDataDir` (or `AcpAgents:InstallRoot`, default `{AgwDataDir}/acp-agents`) used by the running Server:

```bash
./agw-server acp install goose codex-acp
```

Official container images omit Node.js and `uv`. For `npx` or `uvx` ACP distributions, install Node.js or `uv` in a derived execution image and install the agent packages at build time, passing `--AcpAgents:InstallRoot=/opt/agw/acp-agents` to both the installer and the container command.

In Distributed mode, deploy the identical derived image to every Data Plane replica, use `FullAccess`, and mount the agent's own session storage at the same shared path across replicas (for example `CODEX_HOME=/shared/agw-acp/codex`).

## Implementation and references

- [Deployment guide]({{< relref "/docs/operations/configuration" >}})
- [Server extension SDK](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Extensions.Sdk/README.md)
- [TaskPilot extension](https://github.com/zxyao145/agw/blob/main/src/extensions/taskpilot/README.md)
- [Feishu extension](https://github.com/zxyao145/agw/blob/main/src/extensions/feishu/README.md)
- [ACP deployment configuration](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
