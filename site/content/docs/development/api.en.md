---
title: "APIs and execution protocols"
description: "Distinguish management JSON APIs, SignalR execution, and A2A."
weight: 30
lastmod: 2026-09-15
translationKey: docs/development/api
---

Prerequisites: access to a development Server and a valid authenticated identity. Use current OpenAPI and owning-module Contracts for exact fields; this site does not duplicate the complete schema.

## Protocol boundaries

| Interface | Purpose and contract |
| --- | --- |
| Management JSON APIs | Bens.Results `ApiResult` envelopes, unwrapped by typed client helpers |
| `/api/hubs/exec` | SignalR execution commands, state, and events |
| `/api/agents/permission-capabilities` | Query supported permissions for a target |
| A2A | Protocol-specific responses, mapped by Data Plane and Standalone only |
| `/openapi/*` | Development contract entry point; production availability depends on Host configuration |

## Integration sequence

1. Use a named Bearer Token for automation. Browser requests using Cookies to make changes, such as POST, PUT, and DELETE, also need the existing CSRF protection flow to prevent another site from acting through the signed-in session.
2. Read resources accessible to the current user through management APIs and retain their stable identifiers.
3. Query target permission capabilities, then use the existing execution protocol to send commands and subscribe to events.
4. Restore conversation/execution state after reconnecting; disconnection is not completion.

New endpoints default to query/body identifiers under repository rules. Consult current OpenAPI and Contracts for each endpoint’s routes and parameters.

## Handle results in the client

Management JSON APIs use Bens.Results response envelopes. Reuse the typed helpers in `@agw/api` to extract business data, and handle request failures and application errors separately.

An execution connection returns a stream of events. Retain conversation and execution IDs, show tool activity and input requests, and recover actual progress after reconnecting. Partial text is not completion, and disconnection is not cancellation. See the [execution protocol](https://github.com/zxyao145/agw/blob/main/docs/ws-flow.md) for message shapes and order.

## Contract changes

Keep DTOs in the owning module's Contracts. Expected application failures use `AgwException` and stable seven-digit ErrorCodes, mapped at boundaries. WebSocket, OAuth redirects, A2A, and static files retain their protocols.

After backend contract changes, run `pnpm gen:api` from `src/clients` and validate callers. Do not hand-edit generated `openapi.d.ts` or log actual Tokens.

## Implementation and references

- [Execution protocol](https://github.com/zxyao145/agw/blob/main/docs/ws-flow.md)
- [API rules](https://github.com/zxyao145/agw/blob/main/docs/rules.md)
- [API client](https://github.com/zxyao145/agw/tree/main/src/clients/packages/api)
