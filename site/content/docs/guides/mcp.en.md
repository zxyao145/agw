---
title: "MCP servers"
description: "Connect tool servers and verify the Server-side network and process environment."
weight: 90
lastmod: 2026-09-15
translationKey: docs/guides/mcp
---

MCP (Model Context Protocol) lets agents use tools from another service. An MCP server describes the operations it offers; AGW connects to it and makes those tools available to a custom agent. The available operations depend on the service.

Prepare the service’s launch command or URL, transport, and required credentials. The model itself is configured separately through a Model Provider.

## Connect

1. Create a tool server in MCP configuration and supply its required transport, command or endpoint, and credentials.
2. Local-process servers must be launchable on the execution node; remote services must be reachable from that node.
3. Bind the service to a custom agent and start a new turn.
4. Inspect discovered tools and validate a read-only operation.

Directories, commands, and network access belong to the actual Server/execution node. Connecting a remote Desktop does not automatically expose locally installed MCP services to the Server.

![AGW Desktop: configure a stdio MCP Server with its command, arguments, working directory, and required environment variables.](/images/screenshots/mcp-create.png)
{caption="AGW Desktop: configure a stdio MCP Server with its command, arguments, working directory, and required environment variables."}

## Choose a transport

| Transport | How it connects | What to check |
| --- | --- | --- |
| stdio | AGW starts a process and communicates through its input and output | The executable, arguments, and working directory exist on the execution host |
| HTTP / SSE | AGW connects to an already running tool service | The URL and authentication are correct and reachable from the execution host |

A stdio command may work in your personal terminal but be unavailable to a Server running under another account or in a container. Check commands and environment variables in that environment. Test remote URLs from the execution host as well.

## Relationship to Integrations

MCP is a tool protocol. An Integration provides catalog definitions, user configuration, credentials, and a Connection lifecycle. Integrations can themselves expose tools through MCP.

Plugin MCP supports stdio, HTTP, and SSE sources. Plugin HTTP/SSE sources that inject credentials must use HTTPS. Credentials are resolved within the invocation scope; keep them out of public URLs and prompts.

## Troubleshoot

For missing tools, check bindings, launch commands, executables, network reachability, and credentials. Verify the service in the same execution environment before retrying a new agent turn. External CLI MCP configuration follows that tool's mechanism and is not AGW Connection injection.

## Implementation and references

- [Integrations and MCP](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
- [Agent execution](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
