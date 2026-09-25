---
title: "APIs and execution protocols"
description: "Distinguish management JSON APIs, SignalR execution, and A2A."
weight: 30
lastmod: 2026-09-25
translationKey: docs/development/api
---

Prerequisites: access to a development Server and a valid authenticated identity, such as an API Key or a browser session. Use current OpenAPI and owning-module Contracts for exact fields; this site does not duplicate the complete schema.

## Protocol boundaries

| Interface | Purpose and contract |
| --- | --- |
| Management JSON APIs | Bens.Results `ApiResult` envelopes, unwrapped by typed client helpers |
| `/api/hubs/exec` | SignalR execution commands, state, and events; mapped by Data Plane and Standalone only and accepts only the WebSocket transport. The official client connects with `skipNegotiation: true` |
| `/api/agents/permission-capabilities` | Query supported permissions for a target |
| `/api/auth/oidc/providers` | Enabled sign-in providers, used to render the sign-in buttons |
| `/api/auth/oidc/login` | Redirects to the provider; `client` is `web` or `desktop` |
| `/api/auth/desktop/exchange` | Desktop exchanges a one-time code plus its verifier for an API Key |
| A2A | Protocol-specific responses, mapped by Data Plane and Standalone only |
| `/openapi/*` | Contract entry point; served, together with the Scalar API reference, only in the Development environment by Control Plane or Standalone |

Sign-in routes are served by Control Plane and Standalone. An API Key obtained by Desktop behaves like a manually created one and accesses resources as its creator.

## Integration sequence

1. Use an API Key in the `Authorization: Bearer` header for automation. Browser requests using Cookies to make changes, such as POST, PUT, and DELETE, also need the existing CSRF protection flow to prevent another site from acting through the signed-in session.
2. Read resources accessible to the current user through management APIs and retain their stable identifiers.
3. Query target permission capabilities, then use the existing execution protocol to send commands and subscribe to events.
4. Restore conversation/execution state after reconnecting; disconnection is not completion.

New endpoints default to query/body identifiers under repository rules. Consult current OpenAPI and Contracts for each endpoint’s routes and parameters.

## Handle results in the client

Management JSON APIs use Bens.Results response envelopes. Reuse the typed helpers in `@agw/api` to extract business data, and handle request failures and application errors separately.

An execution connection returns a stream of events. Retain conversation and execution IDs, show tool activity and input requests, and recover actual progress after reconnecting. Partial text is not completion, and disconnection is not cancellation. See the [execution protocol](https://github.com/zxyao145/agw/blob/main/docs/ws-flow.md) for message shapes and order.

## Agent structured-response fields

`responseSchema` holds the JSON Schema text configured on an agent. It appears in the full agent response: `GET /api/agents/{id}`, `GET /api/agents/paged`, `POST /api/agents`, `PUT /api/agents/{id}`, and `PUT /api/agents/enabled`. The selector endpoint `GET /api/agents` omits it.

Both response shapes include `resultFormat`, either `markdown` or `json`, derived from whether a schema is configured. Clients use it to decide how to render the final result without parsing the schema themselves.

On update, an absent field keeps the current value, `null` or a whitespace-only string clears it, and any other string replaces it. The server requires valid JSON whose root is an object and otherwise returns an invalid-parameter error. Pi agents reject any schema with an invalid-parameter error. The schema is stored and forwarded as text only.

## Contract changes

Keep DTOs in the owning module's Contracts. Expected application failures use `AgwException` and stable seven-digit ErrorCodes, mapped at boundaries. WebSocket, OAuth redirects, A2A, and static files retain their protocols.

After backend contract changes, export the Development OpenAPI document to `src/clients/packages/api/openapi.json`, run `pnpm gen:api` from `src/clients`, and validate callers. `gen:api` converts only that local file; it does not fetch the latest document from Server. Do not hand-edit generated `openapi.d.ts` or log actual API Keys.

## Implementation and references

- [Execution protocol](https://github.com/zxyao145/agw/blob/main/docs/ws-flow.md)
- [API rules](https://github.com/zxyao145/agw/blob/main/AGENTS.md)
- [API client](https://github.com/zxyao145/agw/tree/main/src/clients/packages/api)
