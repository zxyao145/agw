---
title: "Chat and execution history"
description: "Run conversations, attach images, handle human input, and inspect execution state."
weight: 40
lastmod: 2026-09-15
translationKey: docs/guides/chat
---

Chat is where you send tasks to an agent or agentflow and inspect the results. Keep related questions in one conversation to retain the discussion and tool activity; start another conversation for a different topic.

Connect to the intended Server and prepare a working agent or agentflow. For an initial setup, follow [Your first conversation]({{< relref "/docs/start/first-chat" >}}).

## An interaction

1. Open Chat and select the Project and execution target.
2. Enter the task, optionally attach images, and send it.
3. Read streamed output and tool activity. Handle approval or user input requests in the conversation.
4. Review the conversation history and final execution state.

Web, Desktop, and Mobile accept text plus JPEG, PNG, GIF, or WebP images. A message may contain up to five images, each at most 5 MB and at most 10 MB combined. Image understanding also depends on the model.

![AGW Desktop chat: select a Project and Agent at the top, then enter a message below.](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop chat: select a Project and Agent at the top, then enter a message below."}

## Understand progress

| What you see | What to do |
| --- | --- |
| Replies or tool activity keep arriving | Wait for completion and watch for errors |
| A tool approval request | Inspect the operation, arguments, and paths before deciding |
| A request for information | Answer in the current conversation so work can continue |
| Execution has finished | Check the reply and, for file work, the actual files or diff |
| An error or lost connection | Check execution state before retrying to avoid duplicate actions |

A successful execution still needs a result check. For a documentation edit, inspect the changes as well as the agent’s completion message.

## State and connections

Closing a page or losing a connection does not mean execution was canceled. Use the explicit interrupt action to stop a task. After reconnecting, inspect whether the task is running, awaiting input, or finished.

Desktop gives each Server/Project/Conversation combination an independent execution connection. Switching Project tabs detaches the visible subscriber without automatically stopping background work.

## History

The server persists conversation and tool activity in batches. The Host template sets a 10-second flush interval; code falls back to five seconds when omitted. Live output that has not yet flushed is not confirmed durable history.

If the UI looks wrong, first check the selected Server and conversation, then pending input requests and Server logs. Workspace and permission changes take effect on the next turn.

## Implementation and references

- [Conversation persistence](https://github.com/zxyao145/agw/blob/main/docs/operations/conversation-persistence.md)
- [Execution connections](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
