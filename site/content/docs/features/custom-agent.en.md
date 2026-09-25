---
title: "Custom agents"
description: "Combine models, instructions, and tools so agents can reason and act within a defined role."
weight: 1
lastmod: 2026-09-25
translationKey: docs/features/custom-agent
---

## Define an agent for your task

In AGW, you can combine a model, instructions, and tools into a reusable agent. It assesses the task and context, selects available tools to act, and uses the results to continue its work.

For example, create a documentation reviewer that reads material, identifies unclear passages, and suggests revisions. Add the appropriate tools and permissions when it needs to edit files directly.

## What you can customize

| Setting | What it determines |
| --- | --- |
| Model Provider | The model used to understand tasks and generate replies |
| Instructions | Role, scope, processing requirements, and output format |
| Tools and Skills | Available operations, task guidance, and capabilities |
| Configured integration connections | External services or accounts the agent can access |
| Response Schema | Whether replies return structured data that follows a JSON Schema; see [Structured responses with JSON Schema]({{< relref "/docs/features/structured-output" >}}) |

Create agents for organizing material, explaining code, and reviewing results. Select them in Chat for individual tasks, or use them as execution steps in an Agentflow.

## Get started

1. Prepare a working Model Provider and create a custom agent in Agents.
2. Select the model and define its role and output, such as “Review documentation and list the original text, issue, and suggested revision.”
3. Add the required tools, Skills, or configured integration connections. Check the Project workspace for file operations.
4. Save and enable the agent, try a short passage in Chat, and inspect its reply, tool calls, and actual results.

## From one agent to a defined process

An individual agent can decide how to complete a task within its role. When every run must follow explicit steps, such as organizing material, reviewing it, and requesting human confirmation, use [Agentflow]({{< relref "/docs/features/agentflow" >}}) to arrange those steps.

An agent's access depends on its configured tools and permissions. Instructions do not grant access to files or external services. Model judgments and execution results still need verification.

[Create a custom agent]({{< relref "/docs/guides/agents" >}}) · [Configure Tools and Skills]({{< relref "/docs/guides/tools-skills" >}})
