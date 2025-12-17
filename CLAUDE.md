# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

D-System is an ASP.NET Core + EF Core backend for managing LLM agents, models, providers, and API keys. It uses Clean Architecture with anemic domain models operated through domain services, following a generic repository + unit of work pattern.

The system is designed to bridge LLM configuration data with runtime agent execution, preparing data for consumption by the Microsoft Agent Framework.

## Architecture & Project Structure

### Layer Organization
```
src/backend/
├── DSystem.Domain/          # Core business logic layer
│   ├── Entities/            # Anemic domain models (Agent, LlmModel, Provider, etc.)
│   ├── Models/              # DTOs for runtime (AiAgent)
│   ├── Services/            # Domain services (*DomainService, AgentRuntimeService)
│   ├── Repositories/        # Repository abstractions (IRepository<T>, IUnitOfWork)
│   └── Enums/               # Domain enumerations
├── DSystem.Infrastructure/  # Data access implementation
│   ├── Data/                # LlmDbContext with EF Core configuration
│   ├── Repositories/        # Concrete implementations (EfRepository, UnitOfWork)
│   └── Configuration/       # Database provider settings
├── DSystem.Manager.Api/     # HTTP API layer
│   ├── Controllers/         # REST controllers (*Controller.cs)
│   └── Contracts/           # Request/response DTOs (*Requests.cs)
├── DSystem.Api/             # Shared API scaffolding
└── DSystem.Host/            # ASP.NET Core host + DI wiring
    └── Program.cs           # Entry point, service registration
```

### Key Architectural Patterns

**Domain Model Relationships:**
- `Agent` → references `ModelProviderApiKey` → references `ModelProvider` (composite key: ModelId + ProviderId)
- `ModelProvider` → bridges `LlmModel` and `Provider` with pricing/limits metadata
- All entities inherit from `BaseEntity` (contains CreatedAt, UpdatedAt, CreatedBy, UpdatedBy)

**AgentRuntimeService:**
Core service at `/src/backend/DSystem.Domain/Services/AgentRuntimeService.cs:33` that hydrates a full `AiAgent` DTO from persisted entities. It traverses the graph: Agent → ModelProviderApiKey → ModelProvider → (LlmModel, Provider) and validates API key enablement before constructing the runtime payload.

**Database Provider Strategy:**
Infrastructure layer supports SQLite (default), PostgreSQL via provider switching in `DependencyInjection.cs:21`. Connection strings and provider selection live in `appsettings.json` under the `Database` section.

## Build & Development Commands

### Restore & Build
```bash
dotnet restore D-System.slnx
dotnet build D-System.slnx
```

### Run Locally
```bash
dotnet run --project src/backend/DSystem.Host
```
OpenAPI endpoint available at `/openapi` when running in Development mode.

### EF Core Migrations
```bash
# Add migration
dotnet ef migrations add <MigrationName> \
  -p src/backend/DSystem.Infrastructure \
  -s src/backend/DSystem.Host

# Update database
dotnet ef database update \
  -p src/backend/DSystem.Infrastructure \
  -s src/backend/DSystem.Host
```

**Important:** Always specify both `-p` (project containing DbContext) and `-s` (startup project with configuration).

### Testing
```bash
dotnet test D-System.slnx
```
No test projects exist yet. When adding tests, place them under `tests/DSystem.*.Tests/` mirroring the namespace structure.

### Code Formatting
```bash
dotnet format
```
Run before commits to maintain consistent styling.

### Running a Single Test
```bash
# When tests are added, run individual test methods:
dotnet test --filter "FullyQualifiedName~AgentRuntimeServiceTests.Method_Condition_ExpectedResult"
```

## Configuration

### Database Settings (`src/backend/DSystem.Host/appsettings.json`)
```json
{
  "Database": {
    "Provider": "sqlite",  // or "postgres"/"postgresql"
    "ConnectionString": "Data Source=llmmanager.db"
  }
}
```

Switch to PostgreSQL by changing `Provider` to `"postgres"` and providing a full connection string. Keep sensitive connection strings in environment variables; avoid committing secrets to `appsettings*.json`.

### Service Registration
Domain services are manually registered in `Program.cs:11-16`. When adding new domain services, register them as `Scoped` in the same section.

## Entity Constraints & Database Schema

Key constraints configured in `LlmDbContext.OnModelCreating`:
- `Agent.Name`: max 200 chars, required
- `Agent.Instructions`, `Agent.SystemPrompt`: max 4000 chars
- `ModelProviderApiKey.ApiKey`: max 2000 chars, required
- `ModelProvider`: composite key on (ModelId, ProviderId)
- Cascade deletes: ModelProvider → ModelProviderApiKey → Agent

## Coding Style & Naming Conventions

- Use 4-space indentation and follow C# conventions:
  - `PascalCase` for types and DTO fields
  - `camelCase` for locals and parameters
  - `I` prefix for interfaces
  - `*Controller.cs` for MVC controllers
- Keep DTOs under `Contracts` and domain types under `Entities`/`Services`
- Keep methods async when doing I/O; avoid synchronous EF Core calls
- Favor constructor injection for dependencies

## Testing Guidelines

When adding tests (under `tests/DSystem.*.Tests/`):
- Use xUnit with clear Arrange/Act/Assert sections
- Name test methods: `Method_Condition_ExpectedResult`
- For data access tests, use SQLite in-memory or a disposable file with migrations applied

## Commit Conventions

Follow Conventional Commits format matching existing history:
- `feat:` for new features (e.g., `feat: add Agent entity`)
- `fix:` for bug fixes
- `refactor:` for code restructuring
- `chore:` for maintenance tasks
- `docs:` for documentation changes
- `test:` for adding/updating tests

## Pull Request Guidelines

- One feature per PR
- Include a short summary, linked issue, and testing notes (commands run, migration impact)
- Add screenshots or curl examples for API changes when helpful
- Ensure PRs build and `dotnet test` passes before requesting review
- Highlight breaking changes or migration requirements explicitly

## Frontend Development

### Tech Stack
- **Framework**: Next.js 16 with App Router
- **UI**: React 19.2, Tailwind CSS 4, Radix UI components
- **State**: TanStack React Query 5.90
- **API Client**: OpenAPI-generated types with typed fetch wrapper

### Commands
```bash
cd src/frontend/web

# Install dependencies
pnpm install

# Development server
pnpm dev

# Production build
pnpm build

# Generate API types from backend OpenAPI spec
pnpm gen:openapi
```

### Frontend Architecture
```
src/frontend/web/src/
├── api/
│   ├── client.ts          # Typed API client (fetch wrapper)
│   └── openapi.d.ts       # Auto-generated from backend/openapi.json
├── app/
│   ├── (app)/             # Main app routes with shared layout
│   │   ├── agents/        # Agent CRUD
│   │   ├── models/        # Model management
│   │   ├── providers/     # Provider management
│   │   ├── model-providers/  # Model-Provider associations
│   │   ├── workflows/     # Workflow orchestration
│   │   └── projects/      # Project & task execution
│   └── layout.tsx         # Root layout (providers, theme)
├── components/
│   ├── query-provider.tsx # React Query setup
│   └── ui/                # Radix UI wrappers
└── lib/
    └── utils.ts           # cn() utility
```

**API Integration Pattern**:
- Run `pnpm gen:openapi` after backend schema changes
- Use `apiGet()`, `apiPost()`, `apiPut()`, `apiDelete()` from `api/client.ts`
- All endpoints type-safe via OpenAPI types
- TanStack Query handles caching, refetch, error states

## Extended Domain Model

### Workflow System
**Entities**: `Workflow`, `WorkflowAgent` (join table with ordering)

**Orchestration Patterns** (`WorkflowOrchestrationPattern` enum):
- `Concurrent` (0): Broadcast input to all agents in parallel
- `Sequential` (1): Chain agents A→B→C with output passing
- `GroupChat` (2): Manager-controlled conversation (not implemented)
- `Handoff` (3): Dynamic agent switching (not implemented)
- `Magentic` (4): MagenticOne-inspired pattern (not implemented)

**WorkflowRuntimeService** (`DSystem.Domain/Services/WorkflowRuntimeService.cs`):
- Hydrates workflow agents via `AgentRuntimeService`
- Uses `AgentWorkflowBuilder` from Microsoft.Agents.AI.Workflows
- Executes via `InProcessExecution.StreamAsync()`
- Returns `WorkflowExecutionResult` with chat messages and status

### Project/Task System
**Entities**: `Project`, `ProjectTask`, `ProjectLease`

**ProjectTask Statuses**: `Pending` → `Running` → `Succeeded`/`Failed`/`Canceled`

**Background Scheduler** (`ProjectTaskSchedulerHostedService` in `DSystem.Host`):
- Polls every 2 seconds for pending tasks
- **Concurrency**: Max 4 projects executing in parallel
- **Per-project sequential execution**: Only one task runs per project at a time
- **Distributed locking**: DB-backed `ProjectLease` with 30-second TTL
- **Lock strategy**: Insert (fast path) or update if expired/owned by same instance
- **Ordering**: Tasks executed by `UpdateTime` (FIFO, reorderable)

**Execution Flow**:
1. Acquire project lock via `ProjectLease`
2. Mark task `Pending` → `Running`
3. Execute workflow via `WorkflowRuntimeService`
4. Store result in `OutputJson` or `ErrorMessage`
5. Mark task `Succeeded`/`Failed`
6. Release lock

### Complete Entity Graph
```
LlmModel ←→ ModelProvider ←→ Provider
                ↓
         ModelProviderApiKey
                ↓
              Agent ←→ WorkflowAgent ←→ Workflow
                                          ↓
                                     ProjectTask ←→ Project
                                                      ↓
                                                 ProjectLease (lock)
```

**Key Constraints**:
- `Workflow.ConfigurationJson`: max 16000 chars (workflow-specific settings)
- `ProjectTask.Input`/`Description`: max 4000 chars
- `ProjectTask.OutputJson`: max 16000 chars (serialized execution result)
- `WorkflowAgent`: Unique index on (WorkflowId, Order)
- `ProjectTask`: Composite index on (ProjectId, Status, UpdateTime)

## Development Notes

- All projects target `.NET 10.0` with nullable reference types enabled
- Async/await pattern used throughout for I/O operations
- Constructor injection for all dependencies
- Repository pattern hides EF Core implementation details from domain services
- When modifying entity relationships, update both `LlmDbContext` configuration and entity navigation properties
- Migrations and runtime data are generated relative to the host project; keep repository code free of environment-specific paths
- Frontend uses App Router (not Pages Router); server/client components follow Next.js 16 conventions
- Background services must handle graceful shutdown via `CancellationToken`

## Checkpoint Record

**Project**: D-System | **Time**: 2025-12-17T00:00:00Z
**Milestone**: UI/UX improvements for management pages | **Branch**: main

### Technical Status
- **Code Quality**: Excellent (19,184 files)
- **Architecture Health**: Rapid development phase
- **Dependencies**: Latest (Next.js 16, .NET 10, Microsoft.Agents.AI)

### Documentation Maintenance
- [x] **CLAUDE.md**: Updated with frontend, workflows, and task system
- [x] **Configuration Sync**: Frontend package.json aligned
- [x] **API Documentation**: OpenAPI at `/openapi` endpoint

### Recent Activity (Since 2025-12-16)
- **Period**: 1 day | **Commits**: UI improvements (uncommitted)
- **Major Changes**:
  - Added delete confirmation dialogs (Agents, Models, Providers, Workflows pages)
  - Improved task cancel button UX (disabled for completed tasks)
  - Enhanced user safety with confirmation prompts
- **Activity Intensity**: Medium (UI/UX refinement)
- **Development Trend**: Upward trajectory

### Recommended Actions
1. Add unit/integration tests for `ProjectTaskSchedulerHostedService` (distributed lock edge cases)
2. Implement remaining workflow patterns (GroupChat, Handoff, Magentic)
3. Add frontend error boundaries for API failures
4. Consider optimistic UI updates for better perceived performance
5. Add loading skeletons for better UX

**Git Commit**: `7beca5d` → pending | **Health Score**: 8.5/10
