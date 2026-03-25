# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Agw is an ASP.NET Core + EF Core backend with a Next.js frontend for managing LLM agents, models, providers, tools, skills, jobs, and multi-agent workflows. It uses a modular project structure built around Microsoft.Agents.AI, MCP tool servers, A2A endpoints, and external agents such as Claude Code SDK.

## Architecture & Project Structure

### Backend Organization (`src/backend/`)
```
Agw.Host/              # ASP.NET Core entry point, DI registration, hosted services
Agw.Infrastructure/    # EF Core DbContext, repositories, migrations
Agw.Shared/            # Base entities, enums, shared models, repository interfaces
Agw.Shared.Contract/   # Interfaces for cross-module interaction
Agw.Agents/            # Agent definitions, agentflows, MCP tools, execution services
Agw.Providers/         # LLM models, providers, model-providers, auth configs
Agw.Tasks/             # Projects, tasks, session records, chat history
Agw.Jobs/              # Background jobs, project leases
Agw.A2A/               # A2A protocol implementation for agent discovery/communication
Agw.Skills/            # Skill archive management (ZIP uploads, SKILL.md format)
Agw.Tools/             # Tool discovery and registration system
```

`Agw.slnx` currently includes these backend projects plus `tests/Agw.Agents.Tests`, `tests/Agw.Tasks.Tests`, and `tests/Agw.Skills.Tests`.

### Key Domain Entities

**Agent System:**
- `Agent` - AI agent with SystemPrompt, ModelProviderId, Type (System/External), MCP tool bindings
- `Agentflow` - Multi-agent workflow with nodes, edges, orchestration pattern
- `AgentflowNode` - Node in workflow graph (references Agent or nested Agentflow)
- `AgentflowEdge` - Connection between nodes
- `McpToolServer` - MCP server configuration (stdio/HTTP/SSE transport)

**Provider System:**
- `LlmModel` - LLM model definition
- `Provider` - API provider (OpenAI, Anthropic) with endpoint and auth configs
- `ModelProvider` - Links model to provider with pricing metadata
- `ProviderAuthConfig` - Authentication (ApiKey or Environment variable)

**Task System:**
- `Project` - Workspace with ExtraSetting for agent configuration
- `ProjectTask` - Execution unit with ContextId, AgentType, Status
- `TaskRecord` - Conversation persistence with session tracking
- `ProjectLease` - Distributed lock for concurrent task execution

### Entity Relationships
```
LlmModel ←→ ModelProvider ←→ Provider
                               ↓
                        ProviderAuthConfig

Agent ←→ ModelProvider (optional for External agents)
   ↓
AgentMcpToolServer ←→ McpToolServer
   ↓
AgentflowNode ←→ Agentflow
   ↓
AgentflowEdge (Source/Target)

Project → ProjectTask → TaskRecord
             ↓
        ProjectLease
```

### Core Services

**AgentRuntimeService** (`Agw.Agents/Application/Agents/AgentRuntimeService.cs`):
- Creates `AIAgent` instances from persisted `Agent` entities
- Hydrates provider config, selects random enabled auth config
- Builds tool list from registered functions + MCP tools
- Supports OpenAI and Anthropic providers via Microsoft.Agents.AI
- Handles Claude Code external agents with session resumption

**AgentflowRuntimeService** (`Agw.Agents/Application/Agentflows/AgentflowRuntimeService.cs`):
- Executes multi-agent workflows with different orchestration patterns
- Patterns: Concurrent, Sequential, GroupChat, Handoff, Magentic

**ProjectTaskSchedulerHostedService** (`Agw.Jobs/HostedService/ProjectTaskSchedulerHostedService.cs`):
- Background service polling for pending tasks every 2 seconds
- Max 4 projects executing in parallel, one task per project at a time
- DB-backed `ProjectLease` with 30-second TTL for distributed locking

**ToolRegistryService** (`Agw.Tools/ToolRegistryService.cs`):
- Discovers AI tools from `[AiTool]` attributes and `IAgwTool` implementations
- Singleton service that caches tool metadata on startup
- Creates `AITool` instances for agent execution via `AgwToolFactory`
- Supports tool categories, parameter schemas, and timeout configuration
- Integrates with Microsoft.Extensions.AI for tool registration

**SkillAppService** (`Agw.Skills/Services/SkillAppService.cs`):
- Manages skill archives uploaded as ZIP files
- Extracts archives and validates SKILL.md frontmatter
- Rewrites SKILL.md metadata (name/description) to match database values
- Skills stored in `wwwroot/skills/{skillName}/` directory
- Creates/deletes physical directories on skill CRUD operations

## Build & Development Commands

### Backend
```bash
# Restore and build
dotnet restore Agw.slnx
dotnet build Agw.slnx

# Run locally (default port 5015 on the http profile)
dotnet run --project src/backend/Agw.Host

# Run with hot reload
dotnet watch --project src/backend/Agw.Host

# Run tests
dotnet test Agw.slnx

# EF Core migrations (DO NOT run automatically)
dotnet ef migrations add <MigrationName> \
  -p src/backend/Agw.Infrastructure \
  -s src/backend/Agw.Host

dotnet ef database update \
  -p src/backend/Agw.Infrastructure \
  -s src/backend/Agw.Host
```

### Frontend
```bash
cd src/frontend/web

# Install dependencies
pnpm install

# Development server (port 3000)
pnpm dev

# Production build
pnpm build

# Lint and format
pnpm lint
pnpm lint:fix
pnpm format

# Generate API types from backend OpenAPI spec
pnpm gen:openapi
```

The frontend dev server runs on `http://localhost:3000`. `src/frontend/web/next.config.ts` rewrites `/api/*` and `/openapi/*` to `BACKEND_API_BASE_URL`, which defaults to `http://localhost:5015`.

### Tests
```bash
# Run all tests
dotnet test Agw.slnx

# Run specific test project
dotnet test tests/Agw.Agents.Tests
dotnet test tests/Agw.Tasks.Tests
dotnet test tests/Agw.Skills.Tests
```

Current test projects use xUnit v3.

### Code Formatting
```bash
dotnet format
```

## Configuration

### Backend (`appsettings.json`)
```json
{
  "Database": {
    "Provider": "sqlite",
    "ConnectionString": "Data Source=agw.db"
  },
  "OpenTelemetry": {
    "ServiceName": "Agw",
    "ServiceVersion": "1.0.0",
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

Database providers: `sqlite`, `postgres`. Switch by changing `Provider` and providing appropriate connection string. Store sensitive values in environment variables.

### Service Registration (`Program.cs`)
Domain services are manually registered as `Scoped` or `Singleton`. When adding new services, register them in the same section.

## Coding Style

- 4-space indentation, C# conventions: `PascalCase` for types/members, `camelCase` for locals/parameters
- `I` prefix for interfaces, `*Controller.cs` for controllers
- DTOs under `Contracts/` folders in each module
- Async methods for I/O, constructor injection for dependencies
- Frontend: TypeScript + React function components, kebab-case filenames

## Commit Conventions

Follow Conventional Commits:
- `feat:` new features
- `fix:` bug fixes
- `refactor:` code restructuring
- `chore:` maintenance tasks
- `docs:` documentation
- `test:` tests

## Pull Request Guidelines

- One feature per PR
- Include summary, linked issue, testing notes
- Ensure `dotnet build` and `pnpm lint` pass
- Highlight migration requirements

## Frontend Architecture

### Tech Stack
- Next.js 16 with App Router
- React 19, Tailwind CSS 4, Radix UI components
- TanStack React Query 5 for data fetching
- openapi-fetch with auto-generated types

### Route Structure
```
src/app/(app)/
├── (agents)/
│   ├── agents/           # Agent CRUD
│   ├── agentflows/       # Workflow editor (React Flow)
│   ├── mcp-tool-servers/ # MCP server management
│   └── skills/           # Skill archive management
├── (external-agents)/
│   └── claude-code/      # Claude Code integration UI
├── (overview)/
│   ├── dashboard/        # Dashboard
│   └── traces/           # Trace viewer
├── (providers)/
│   ├── models/           # Model management
│   ├── providers/        # Provider management
│   └── model-providers/  # Model-Provider associations
└── (tasks)/
    ├── projects/         # Project & task execution
    └── jobs/             # Scheduled jobs

src/app/(app)/integrations/ # OAuth-backed integrations UI
```

### API Integration
- Run `pnpm gen:openapi` after backend schema changes
- Use typed `apiGet()`, `apiPost()`, `apiPut()`, `apiDelete()` from `api/client.ts`
- OpenAPI types in `api/openapi.d.ts`
- Use `api/files.ts` for file operations in the Claude Code UI
- Use `api/execution-ws.ts` for task execution websocket flows

## A2A Protocol

Agw exposes JSON-RPC A2A endpoints by default. The mapped prefix is `/api/a2a/`.

**Endpoints:**
- `GET /.well-known/agents.json` - List available agents
- `GET /api/a2a/{agentName}/.well-known/agent-card.json` - Get agent metadata (AgentCard)
- `POST /api/a2a/{agentName}` - JSON-RPC request entrypoint

**Client Example:**
```bash
curl http://localhost:5015/.well-known/agents.json
```

## Agent Types

- **System**: Internal agents using ModelProvider (OpenAI/Anthropic)
- **External**: External agents like Claude Code SDK, configured via `Extra` JSON

## Orchestration Patterns

- **Concurrent**: Broadcast to all agents, collect results independently
- **Sequential**: Chain agents A→B→C with output passing
- **GroupChat**: Round-robin manager-controlled conversation
- **Handoff**: Dynamic agent switching based on context
- **Magentic**: Orchestrator + workers pattern with stall detection

## Tools System

The `Agw.Tools` module provides a tool discovery and registration mechanism for AI agents.

**Tool Registration Methods:**
- **Attribute-based**: Mark public static methods with `[AiTool]` attribute
- **Interface-based**: Implement `IAgwTool` interface with `ExecuteAsync` method

**Tool Metadata:**
- `[AiTool("name", Category = "...")]` - Tool name and category
- `[Description("...")]` - Tool description (also on parameters)
- `[AiToolParameterSchema("string", Format = "...")]` - JSON Schema type hints
- `[AiToolRequired]` - Mark required parameters

**Example:**
```csharp
[AiTool("read_file", Category = "Files", RequiresConfirmation = false, TimeoutMs = 5000)]
[Description("Reads a file from the filesystem")]
public static Task<string> ReadFile(
    [Description("Path to the file to read")] string path)
{
    return Task.FromResult(File.ReadAllText(path));
}
```

Tools are automatically discovered on app startup via `ToolRegistryService` (Singleton) and exposed as `AITool` instances for agent consumption.

## Development Notes

- All projects target `.NET 10.0` with nullable reference types
- Migrations relative to host project; run with `-p` and `-s` flags
- Frontend uses App Router (Next.js 16)
- Background services handle graceful shutdown via `CancellationToken`
- OpenTelemetry integrated for tracing, metrics, and logging
- Serilog for structured logging with OTLP correlation
