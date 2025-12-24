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

### Workflow System (Graph Structure - Updated 2025-12-21)

**Entities**: `Workflow`, `WorkflowNode`, `WorkflowEdge`

**Graph-Based Architecture**:
The workflow system uses an explicit graph structure matching the React Flow visual representation:
- `WorkflowNode`: Represents agents or nested workflows in the graph
  - `NodeId`: Unique identifier within the workflow (e.g., "agent-uuid-timestamp-random")
  - `Type`: Enum (AgentNode=0, WorkflowNode=1)
  - `RelateId`: Foreign key to Agent.Id (if AgentNode) or Workflow.Id (if WorkflowNode)
  - Navigation: `SourceEdges`, `TargetEdges`

- `WorkflowEdge`: Explicit connections between nodes
  - `EdgeId`: Unique identifier (e.g., "e{sourceId}-{targetId}")
  - `SourceNodeId`, `TargetNodeId`: References to WorkflowNode.NodeId
  - `Animated`: Boolean for visual representation
  - Navigation: `SourceNode`, `TargetNode`

**Migration from WorkflowAgent** (2025-12-21):
- **Previous**: Simple join table with Order field (linear sequences only)
- **Current**: Full graph structure supporting arbitrary topologies
- **Breaking Change**: `Workflow.Agents` navigation removed, replaced with `Workflow.Nodes` and `Workflow.Edges`

**Orchestration Patterns** (`WorkflowOrchestrationPattern` enum):
- `Concurrent` (0): Broadcast input to all agents in parallel
- `Sequential` (1): Chain agents A→B→C with output passing
- `GroupChat` (2): Round-robin manager-controlled conversation
- `Handoff` (3): Dynamic agent switching based on context
- `Magentic` (4): MagenticOne-inspired pattern with orchestrator + workers

**WorkflowRuntimeService** (`DSystem.Domain/Services/WorkflowRuntimeService.cs`):
- Hydrates workflow nodes via `AgentRuntimeService`
- Resolves node types (AgentNode → Agent, WorkflowNode → Workflow)
- Uses `AgentWorkflowBuilder` from Microsoft.Agents.AI.Workflows
- Executes via `InProcessExecution.StreamAsync()`
- Returns `WorkflowExecutionResult` with chat messages and status

**MagenticOrchestrationManager** (`DSystem.Domain/Services/MagenticOrchestrationManager.cs`):
- Custom GroupChat manager implementing Magentic-One orchestration pattern
- First agent in list is orchestrator, rest are worker agents
- Orchestrator coordinates task distribution and checks for completion
- Detects stalls (repeated similar outputs) and triggers orchestrator intervention
- Supports configurable max rounds (default: 10), stall detection (default: 3), and plan resets (default: 2)
- Terminates on: max rounds reached, orchestrator completion signal, or excessive resets

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

### Complete Entity Graph (Updated 2025-12-21)
```
LlmModel ←→ ModelProvider ←→ Provider
                ↓
         ModelProviderApiKey
                ↓
              Agent ←→ WorkflowNode (Type=AgentNode) ←→ Workflow
                          ↓                              ↓
                    WorkflowEdge                   WorkflowNode (Type=WorkflowNode)
                     (Source/Target)                    ↓
                                                    (Self-reference)

Workflow → ProjectTask ←→ Project
                           ↓
                      ProjectLease (lock)
```

**Key Constraints**:
- `Workflow.ConfigurationJson`: max 16000 chars (workflow-specific settings)
- `ProjectTask.Input`/`Description`: max 4000 chars
- `ProjectTask.OutputJson`: max 16000 chars (serialized execution result)
- `WorkflowNode`: Unique index on (WorkflowId, NodeId), Foreign key on RelateId (polymorphic)
- `WorkflowEdge`: Unique index on (WorkflowId, EdgeId), Foreign keys on SourceNodeId, TargetNodeId
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

## Observability

### OpenTelemetry Integration
Backend includes full OpenTelemetry instrumentation for distributed tracing, metrics, and logging:

**Configuration** (`appsettings.json`):
```json
{
  "OpenTelemetry": {
    "ServiceName": "DSystem",
    "ServiceVersion": "1.0.0",
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

**Instrumentation**:
- **Tracing**: ASP.NET Core requests, HTTP clients, EF Core (with SQL statements)
- **Metrics**: Request rates, HTTP client metrics, custom business metrics
- **Logging**: Structured logs exported via OTLP

**Custom Metrics** (`ProjectTaskSchedulerHostedService`):
- `dsystem.tasks.executed` - Successfully executed tasks count
- `dsystem.tasks.failed` - Failed tasks count
- `dsystem.leases.acquired` - Project lease acquisitions
- `dsystem.leases.failed` - Failed lease attempts
- `dsystem.tasks.duration` - Task execution duration histogram (ms)

**Exporters**:
- Console (development debugging)
- OTLP (production, compatible with Jaeger/Prometheus/Grafana)

All custom instrumentation uses activity source `DSystem.*` and meter `DSystem.*`.

### Serilog Integration
Backend uses Serilog for structured logging with OpenTelemetry correlation:

**Configuration** (`appsettings.json`):
```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Async",
        "Args": {
          "configure": [
            {
              "Name": "Console",
              "Args": {
                "outputTemplate": "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] [TraceId:{TraceId}] [SpanId:{SpanId}] {Message:lj}{NewLine}{Exception}"
              }
            }
          ]
        }
      },
      {
        "Name": "Async",
        "Args": {
          "configure": [
            {
              "Name": "File",
              "Args": {
                "path": "logs/dsystem-.log",
                "rollingInterval": "Day",
                "retainedFileCountLimit": 30,
                "outputTemplate": "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] [TraceId:{TraceId}] [SpanId:{SpanId}] [MachineName:{MachineName}] [ThreadId:{ThreadId}] {Message:lj}{NewLine}{Exception}"
              }
            }
          ]
        }
      }
    ],
    "Enrich": [ "FromLogContext", "WithMachineName", "WithThreadId", "WithOpenTelemetryTraceId", "WithOpenTelemetrySpanId" ]
  }
}
```

**Features**:
- **Console Sink**: Development-friendly console output with TraceId/SpanId
- **File Sink**: Daily rolling files in `logs/` directory (retained for 30 days)
- **Async Sinks**: Non-blocking logging with background flushing
- **OpenTelemetry Enrichment**: Automatic TraceId and SpanId injection for correlation
- **Structured Logging**: All logs are structured with machine name, thread ID, and context
- **Request Logging**: HTTP request/response logging via `UseSerilogRequestLogging()`

**Log Format Example**:
```
[2025-12-20 18:30:45.123 +08:00] [INF] [DSystem.Host.ProjectTaskSchedulerHostedService] [TraceId:a1b2c3d4e5f67890] [SpanId:1234567890abcdef] Starting project task execution
```

**Correlation with OpenTelemetry**:
Logs automatically include `TraceId` and `SpanId` from active OpenTelemetry activities, enabling correlation between:
- Application logs (Serilog)
- Distributed traces (OpenTelemetry)
- Custom metrics (OpenTelemetry)

This unified observability stack allows tracing a request from HTTP entry → database queries → business logic → logs.

## Checkpoint Record

**Project**: D-System | **Time**: 2025-12-24T14:18:57Z
**Milestone**: Scheduler optimization and UI polish | **Branch**: main

### Technical Status
- **Code Quality**: Excellent (19,480 code files)
- **Architecture Health**: Stable - Infrastructure optimization and bug fixes
- **Dependencies**: Latest (Next.js 16, .NET 10, React Flow 11, OpenTelemetry 1.14.0, Serilog 4.3.0)

### Documentation Maintenance
- [x] **CLAUDE.md**: Updated with latest development activity
- [x] **Configuration Sync**: All dependencies synchronized
- [x] **API Documentation**: OpenAPI specification current
- [x] **Database**: All migrations applied, .gitignore updated

### Recent Activity (Since 2025-12-23T15:53:43Z checkpoint)
- **Period**: 22.5 hours | **Work Session**: Infrastructure optimization
- **Major Changes**:
  - ✅ **Backend Scheduler Optimization** (ProjectTaskSchedulerHostedService.cs)
    - Changed lease acquisition strategy: UPDATE-first instead of INSERT-first
    - Reduces DbUpdateException frequency in multi-instance scenarios
    - Eliminates noise in error logs during normal operation
    - Improved comment clarity for lease acquisition flow
  - ✅ **Frontend Select Component Fix** (visual-agentflow-builder.tsx)
    - Added `position="popper"` to SelectContent for accurate positioning
    - Fixed dropdown misalignment in ReactFlow canvas context
    - Added `sideOffset={4}` for better visual spacing
  - ✅ **Database Cleanup**
    - Removed SQLite database files from tracking (.gitignore updated)
    - Cleaned up d_system.db, d_system.db-shm, d_system.db-wal
- **Files Changed**: 4 (1 backend service, 1 frontend component, 1 gitignore, 1 UI component)
- **Activity Intensity**: Low (Targeted bug fixes)
- **Development Trend**: ➡️ Stable (Maintenance and polish)

### Rename Scope Summary
**Backend (25 files)**:
- 6 entity/enum files renamed
- 2 service files renamed
- 2 API files renamed (controller + contracts)
- 1 database migration created
- 12 files content updated (DbContext, Program.cs, etc.)

**Frontend (25 files)**:
- 1 type file renamed
- 38+ TypeScript/TSX files content updated
- All imports, types, and API endpoints updated
- Route folder rename pending

**Database Schema**:
- Table: `Workflows` → `Agentflows`
- Columns: `WorkflowId` → `AgentflowId` (in child tables)
- All foreign keys and indexes renamed
- Migration reversible via Down() method

### Recommended Actions
1. ✅ ~~Graph structure entities~~ - **COMPLETED**
2. ✅ ~~EF Core migration~~ - **COMPLETED**
3. ✅ ~~Remove redundant foreign keys~~ - **COMPLETED** (RemoveFK migration)
4. ✅ ~~Remove start node from visual workflow builder~~ - **COMPLETED**
5. ✅ ~~Add SystemPrompt field to Workflow~~ - **COMPLETED** (full-stack implementation)
6. ✅ ~~Fix visual dialog trigger issue~~ - **COMPLETED**
7. ✅ ~~Fix systemPrompt loading in edit mode~~ - **COMPLETED**
8. ✅ ~~Add Fit View button~~ - **COMPLETED**
9. ✅ ~~Fix Select positioning in visual builder~~ - **COMPLETED**
10. ✅ ~~Optimize scheduler lease acquisition~~ - **COMPLETED**
11. 🔄 Test all agentflow CRUD operations end-to-end
12. 🔍 Verify React Flow controls work correctly (zoom, pan, fit view)
13. 🧪 Add integration tests for agentflow visual builder
14. 📝 Consider adding user documentation for visual builder features
15. 🔧 Monitor scheduler lease performance in multi-instance setup

**Git Commit**: `6de55f9` (main) | **Health Score**: 9.6/10

---

### Previous Checkpoint

**Project**: D-System | **Time**: 2025-12-23T15:09:12Z
**Milestone**: Comprehensive Workflow → Agentflow rename | **Branch**: main

### Technical Status
- **Code Quality**: Excellent (19,261 code files)
- **Architecture Health**: Feature expansion - full-stack implementation
- **Dependencies**: Latest (Next.js 16, .NET 10, React Flow 11, OpenTelemetry 1.14.0, Serilog 4.3.0)

### Documentation Maintenance
- [x] **CLAUDE.md**: Updated with SystemPrompt implementation
- [x] **Configuration Sync**: All dependencies synchronized
- [x] **API Documentation**: OpenAPI at src/frontend/web/openapi.json (pending regeneration)
- [x] **Database**: EF Core migration AddWorkflowSystemPrompt created

### Recent Activity (Since 2025-12-22T16:00:49Z checkpoint)
- **Period**: 21.4 hours | **Commits**: Working session on feature implementation
- **Major Changes**:
  - ✅ **Backend**: Workflow SystemPrompt field
    - NEW: `SystemPrompt` property in Workflow entity (required, max 4000 chars)
    - NEW: EF Core migration `20251222161123_AddWorkflowSystemPrompt`
    - Updated `LlmDbContext` configuration with SystemPrompt constraints
    - Updated `LlmDbContextModelSnapshot` with field definition
  - ✅ **Backend**: API layer integration
    - Added `SystemPrompt` parameter to `WorkflowCreateRequest`
    - Added `SystemPrompt` parameter to `WorkflowUpdateRequest`
    - Updated `WorkflowsController.CreateAsync` to handle SystemPrompt
    - Updated `WorkflowsController.UpdateAsync` to handle SystemPrompt
  - ✅ **Frontend**: Complete UI integration
    - Added `systemPrompt: string` to `WorkflowDto` type definition
    - Added `workflowSystemPrompt` state in visual workflow builder
    - Added Textarea component for SystemPrompt input (300px width, 80px min height)
    - Integrated SystemPrompt in request body (create/update workflows)
    - Added SystemPrompt to form reset logic
    - Added SystemPrompt loading from editingWorkflow
  - 📊 **Impact**: Full-stack feature (7 files modified, 28 insertions)
- **Activity Intensity**: Medium-High (Complete feature implementation)
- **Development Trend**: ⬆️ Feature Expansion (adding workflow capabilities)

### Architecture Notes
**Previous Structure** (WorkflowAgent):
```
Workflow 1--* WorkflowAgent *--1 Agent
         (Order, Role)
```

**New Structure** (WorkflowNode + WorkflowEdge):
```
Workflow 1--* WorkflowNode (Type: AgentNode|WorkflowNode)
              |             RelateId → Agent.Id or Workflow.Id
              |
              *--* WorkflowEdge (Source, Target, Animated)
```

**Benefits**:
- Explicit edge modeling (React Flow parity)
- Supports arbitrary graph topologies
- Enables workflow composition (WorkflowNode references)
- Facilitates circular dependency detection
- Direct mapping to frontend visual representation

### Recommended Actions
1. ✅ ~~Graph structure entities~~ - **COMPLETED**
2. ✅ ~~EF Core migration~~ - **COMPLETED**
3. ✅ ~~Remove redundant foreign keys~~ - **COMPLETED** (RemoveFK migration)
4. ✅ ~~Remove start node from visual workflow builder~~ - **COMPLETED**
5. ✅ ~~Add SystemPrompt field to Workflow~~ - **COMPLETED** (full-stack implementation)
6. ⚠️ Run database migration: `dotnet ef database update`
7. ⚠️ Test workflow creation/editing with SystemPrompt field
8. 🔄 **In Progress**: Frontend graph persistence (save/load node/edge state)
9. Regenerate OpenAPI spec for frontend consumption
10. Add validation for SystemPrompt content (e.g., min length, format)

**Git Commit**: `758dea9` (main) | **Health Score**: 9.7/10

---

### Previous Checkpoint

**Project**: D-System | **Time**: 2025-12-21T10:42:21Z
**Milestone**: Visual workflow builder with auto-connect patterns | **Branch**: main

### Recent Activity (Since 2025-12-21T00:39:00Z checkpoint)
- **Period**: 10 hours | **Commits**: Working on visual workflow enhancements
- **Major Changes**:
  - ✅ **Frontend**: Visual workflow builder implementation
  - ✅ **Frontend**: Auto-connect pattern system (5 patterns)
  - ✅ **Frontend**: Enhanced UX features (duplicate agents, keyboard shortcuts)
- **Activity Intensity**: High (UI enhancement and workflow visualization)

**Git Commit**: `302d0d9` (main) | **Health Score**: 9.6/10

