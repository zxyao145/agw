# Agw Web

Next.js 16 admin UI for Agw. This app manages agents, agentflows, providers, models, MCP tool servers, skills, projects, jobs, traces, integrations, and the Claude Code external-agent experience.

## Requirements

- Node.js 20+
- `pnpm`
- Agw backend running on `http://localhost:5015` by default

## Local Development

Run from `src/frontend/web`:

```bash
pnpm install
cp .env.local.example .env.local
pnpm dev
```

The dev server starts on `http://localhost:3000`.

`next.config.ts` rewrites:

- `/api/*` -> `${BACKEND_API_BASE_URL}/api/*`
- `/openapi/*` -> `${BACKEND_API_BASE_URL}/openapi/*`

If you keep the backend on the default ASP.NET Core `http` profile, the example `.env.local` value already points to `http://localhost:5015`.

## Common Commands

```bash
pnpm dev
pnpm build
pnpm lint
pnpm lint:fix
pnpm format
pnpm gen:openapi
```

Notes:

- `pnpm lint` uses `oxlint`.
- `pnpm format` uses `oxfmt`.
- Regenerate `src/api/openapi.d.ts` after backend contract changes.

## Project Structure

```text
src/app/
  layout.tsx
  page.tsx
  (app)/
    (agents)/
      agents/
      agentflows/
      mcp-tool-servers/
      skills/
    (external-agents)/
      claude-code/
    (overview)/
      dashboard/
      traces/
    (providers)/
      models/
      providers/
      model-providers/
    (tasks)/
      projects/
      jobs/
    integrations/
src/api/            # Typed request helpers, websocket helpers, generated OpenAPI types
src/components/     # Shared UI components
src/lib/            # App-side helpers such as execution streaming
```

## API Conventions

- Prefer `src/api/client.ts` for typed `apiGet`, `apiPost`, `apiPut`, and `apiDelete` calls.
- Use `src/api/execution-ws.ts` for task execution websocket flows.
- Use `src/api/files.ts` for Claude Code file operations exposed through the backend.

## Related Files

- `next.config.ts`: local proxy configuration
- `.env.local.example`: backend base URL example
- `openapi.json`: backend schema snapshot used by `pnpm gen:openapi`
