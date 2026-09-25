---
title: "What is AGW?"
description: "Understand agents, projects, chat, agentflows, and jobs."
weight: 10
lastmod: 2026-09-25
translationKey: docs/start/overview
---

AGW is a self-hosted agent workspace for individuals and small engineering teams. It can also serve as an agent gateway. A shared interface brings together custom agents and external agents such as Claude Code, Codex, and Pi, with conversations and execution records organized around projects.

![AGW Desktop chat: select a Project and Agent, then enter a message.](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop chat: select a Project and Agent, then enter a message."}

## Core concepts

| Concept | Purpose |
| --- | --- |
| Model Provider | Connect a provider and model into a usable model configuration |
| Agent | Configure instructions, a model, and capabilities, or connect an external agent |
| Project | A task space containing working directories, context, and conversations |
| Chat | Start interactive execution, inspect messages and tool activity, and answer human input requests |
| Agentflow | Connect nodes into an executable workflow |
| Job | Trigger an agent or agentflow once, at an interval, or on a Cron schedule |

See [Core concepts]({{< relref "/docs/start/concepts" >}}) for the distinctions and how they work together.

## What you can do with AGW

For a code project, ask one agent to explain unfamiliar code, then ask another to review a change. The Project’s conversations retain the discussion and results. Once a task works manually, schedule recurring work such as a weekly progress summary.

| Need | Where to start |
| --- | --- |
| Ask a question or edit a passage | Create an agent and send the task in Chat |
| Read or change project files | Set a Project workspace and configure the necessary tools |
| Continue with a different agent | Switch agents in the same conversation and describe the next task |
| Follow a repeatable sequence | Connect steps in an Agentflow, adding human confirmation where needed |
| Repeat work on a schedule | Create a Job and inspect the outcome of each run |

## Where tasks run

**Server** is the program that runs AGW. Web, Desktop, and Mobile are clients used to operate it. When connected to a remote Server, agents use files, commands, and tools on that host. Opening a conversation on a phone does not move execution to the phone.

For a first local installation, Desktop Full includes Server. For browser access, deploy Docker or Portable Server. Configure a model service separately; using a remote model sends model input outside the AGW host.

## Choose a deployment

**Standalone** runs management, conversations, and scheduled jobs in one Server. It uses SQLite by default and suits local use or a single host.

**Split Control/Data Plane** deployment assigns management and scheduling to Control Plane and execution to Data Plane. It supports separate service deployment and additional execution nodes, but requires shared PostgreSQL, keys, workspaces, and request routing.

Start with Standalone for a simple setup. See [Installation]({{< relref "/docs/start/install" >}}) for packages, or [Split deployment]({{< relref "/docs/operations/split" >}}) for deployment and routing details.

## Start with a small task

1. [Install and configure Server]({{< relref "/docs/start/install" >}}) and initialize the server.
2. Configure one working model and create an agent.
3. Send a simple question in Chat to verify the model and execution path.
4. Add a Project for files, an Agentflow for fixed steps, and a Job for recurring work when needed.

Execution records live in your server database. Self-hosting does not mean inference always stays on your computer: selecting a remote model provider sends requests to that provider.

## Current boundaries

AGW is pre-1.0. It suits clearly defined tasks and human-agent collaboration. Complex work still needs clear inputs, completion criteria, and human review. Authentication uses an administrator login, third-party account sign-in, and API Keys; roles and per-key permission scopes are not available.

## Implementation and references

- [Product overview](https://github.com/zxyao145/agw/blob/main/README.md)
- [Authentication](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Auth/README.md)

- [Host composition](https://github.com/zxyao145/agw/blob/main/src/server/Agw.ControlPlane.Host/ControlPlaneHostModule.cs)
- [Execution routes](https://github.com/zxyao145/agw/blob/main/src/server/Agw.DataPlane.Host/DataPlaneHostModule.cs)
- [Ingress routing](https://github.com/zxyao145/agw/blob/main/deploy/nginx.split.conf.example)
- [Execution providers](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
