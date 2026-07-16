# Default Backend Port 30815 Design

## Goal

Change every runtime, development, deployment, and documented default backend entry point from port `5015` to `30815`.

## Scope

- Change the portable server fallback URL to `http://127.0.0.1:30815`.
- Change the HTTP URLs in the ASP.NET Core launch profiles to port `30815`; keep the existing HTTPS port unchanged.
- Change the Next.js backend proxy fallback to `http://localhost:30815`.
- Change the checked-in OpenAPI server example to port `30815`.
- Change Docker host-port mappings and Caddy/Nginx upstream examples to port `30815`; keep the container's internal port `8080` unchanged.
- Update repository instructions, setup/development/deployment guidance, and API examples that describe or use the default backend entry point.
- Keep `AGENTS.md` and `CLAUDE.md` byte-for-byte identical.

## Non-goals

- Do not change environment-variable override behavior.
- Do not introduce a shared port abstraction across C#, TypeScript, container configuration, and documentation.
- Do not change test fixtures where `5015` is merely an arbitrary caller-provided URL rather than a product default.
- Do not change the HTTPS development port or container-internal port.

## Verification

- Search the runtime configuration, deployment examples, and documentation for stale default-port references.
- Confirm `AGENTS.md` and `CLAUDE.md` remain identical.
- Build the backend host and run the relevant web configuration check.
- Review the final diff to ensure every changed line traces to the requested port update.
