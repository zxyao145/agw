---
title: "Logs and troubleshooting"
description: "Diagnose connection, authentication, model, file, and execution failures."
weight: 50
lastmod: 2026-09-15
translationKey: docs/operations/troubleshooting
---

First locate the failing step: opening the page, signing in, calling a model, or using files and tools. Reproduce the problem with a small task so that each check narrows the cause.

Record the Server, Project, conversation, and time, then inspect the matching logs. In a split deployment, check Control Plane for management problems and Data Plane for execution problems.

## Diagnostic order

| Symptom | Check first |
| --- | --- |
| UI unavailable | Listener, port, container mapping, proxy target |
| Setup fails | Database connection, writable directories, remote Setup Code |
| Login or Token fails | Actual Server, revoked Token, database auth state |
| No agent response | Model Provider, model ID, credentials, pending approval/input |
| CLI cannot start | Execution-node executable, account, environment |
| Files missing | Selected root, Server path, mounts, permissions |
| Job does not run | Enablement, future timestamp, UTC Cron, valid target, logs |
| Wrong state after disconnect | Conversation selection, WebSocket proxy, background execution |

## Example: the page opens but the agent does not reply

1. Check whether Chat is waiting for approval or information, and respond if needed.
2. Send a plain text question through the same model connection. If it fails, check the API endpoint, model ID, and credentials.
3. If text works but a tool task fails, check tool bindings, directories, and host permissions.
4. In split deployments, a working management page with a failed chat connection suggests checking that `/api/hubs/exec` routes to Data Plane and supports WebSocket.
5. Find the specific error in Server logs at the recorded time. Repeat the same small task after the fix.

This sequence separates model, tool, and connection failures without changing several settings at once.

## Logs and telemetry

`AgwLogDir` defaults to `./logs` and does not follow `AgwDataDir`. Inspect the relevant role's logs in split deployments. Configure `OpenTelemetry:OtlpEndpoint` for centralized telemetry. Blank or missing values fall back to `http://localhost:4317`; they do not disable export.

History uses Interval batch writes. The Host template sets `ConversationHistory:FlushIntervalSeconds` to 10 seconds; omitted configuration falls back to five seconds. Live output and committed history can differ temporarily.

## Web development proxy

Web development runs on `3001`, with backend default `30816`. Proxy target precedence is `BACKEND_API_BASE_URL`, then `NEXT_PUBLIC_API_BASE_URL`, then the local default. Static export does not use the Next.js proxy; Server or ingress must supply same-origin routing.

After a fix, repeat the small failing task and verify both UI state and logs. When reporting an issue, include version, deployment method, reproduction steps, and sanitized errors, without real Tokens or complete OAuth responses.

## Implementation and references

- [Runtime consistency](https://github.com/zxyao145/agw/blob/main/docs/operations/backend-runtime-consistency.md)
- [Host settings](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Host/appsettings.json)
- [Deployment](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
