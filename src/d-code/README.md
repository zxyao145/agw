# D-Code Example Project

This folder contains a standalone, minimal example project that hosts the Claude Code UI and its WebSocket backend.

## Backend (ASP.NET Core)

The backend lives under `src/d-code/backend` with a minimal host project that serves the Claude Code WebSocket controller.

### Requirements

- .NET 10 SDK
- Claude Code credentials (e.g. `ANTHROPIC_AUTH_TOKEN`) or a custom API base URL

### Run

```bash
cd src/d-code/backend

dotnet restore

dotnet run --project DSystem.DCode.Host
```

The backend will listen on the default ASP.NET Core URL (e.g. `http://localhost:5015`).

> Note: `ClaudeCodeAgentDbSeeder.cs` is included for reference but excluded from compilation because it depends on the full D-System database model.

## Frontend (Next.js)

The frontend lives under `src/d-code/frontend` and mounts the extracted Claude Code UI.

### Run

```bash
cd src/d-code/frontend

pnpm install
pnpm dev
```

By default, the frontend proxies `/api/*` to `http://localhost:5015`. You can override this with:

```bash
export DCODE_BACKEND_BASE_URL=http://localhost:5015
```

Then open `http://localhost:3000/claude-code`.
