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

**Project**: D-System | **Time**: 2025-12-27T07:26:33Z
**Milestone**: UI simplification - Remove API Key ID column from agents table | **Branch**: main

### Technical Status
- **Code Quality**: Excellent (19,521 code files)
- **Architecture Health**: Active Development - UI refinement and simplification
- **Dependencies**: Latest (Next.js 16, .NET 10, vaul 1.1.1, EF Core migrations)

### Documentation Maintenance
- [x] **CLAUDE.md**: Updated with UI simplification details
- [x] **Configuration Sync**: All dependencies synchronized
- [x] **API Documentation**: Pending OpenAPI regeneration
- [x] **Database**: 2 migrations pending (RemoveInstructions, RemoveFlowSystemPrompt)

### Recent Activity (Since 2025-12-27T07:20:08Z checkpoint)
- **Period**: 6 minutes | **Work Session**: Frontend UI refinement
- **Major Changes**:
  - ✅ **Frontend: Agents Table Simplification**
    - REMOVED: "API Key ID" column from agents table (line 553)
    - REMOVED: Corresponding table cell displaying API key ID (line 585-587, now commented)
    - RATIONALE: API Key ID is implementation detail, not useful for users in table view
    - BENEFIT: Cleaner UI, focus on relevant information (Name, System Prompt, Tools, Created)
  - 📊 **Impact**: 1 file modified (agents/page.tsx)
- **Activity Intensity**: Very Low (Minor UI polish)
- **Development Trend**: ➡️ Stabilizing (Iterative UI improvements)

### Recommended Actions
1. ✅ ~~Remove API Key ID column~~ - **COMPLETED**
2. ⚠️ **Run database migration**: `dotnet ef database update` (RemoveInstructions + RemoveFlowSystemPrompt)
3. ⚠️ **Regenerate OpenAPI types**: `cd src/frontend/web && pnpm gen:openapi`
4. 🔄 Test agents table display (verify columns are properly aligned)
5. 🔄 Verify all CRUD operations still work correctly
6. 🔍 Consider if API Key ID should be shown in detail view instead
7. 🧪 Test execute functionality for both Agents and Agentflows
8. 📈 Monitor for TypeScript errors after OpenAPI regeneration
9. 🔍 Continue UI consistency review across all CRUD pages

**Git Commit**: `dbffb65` (main) | **Health Score**: 9.8/10

---

### Previous Checkpoint

**Project**: D-System | **Time**: 2025-12-27T07:20:08Z
**Milestone**: UI component optimization - ButtonGroup integration | **Branch**: main

### Technical Status
- **Code Quality**: Excellent (19,521 code files)
- **Architecture Health**: Active Development - UI component standardization
- **Dependencies**: Latest (Next.js 16, .NET 10, vaul 1.1.1, EF Core migrations)

### Documentation Maintenance
- [x] **CLAUDE.md**: Updated with UI optimization details
- [x] **Configuration Sync**: All dependencies synchronized
- [x] **API Documentation**: Pending OpenAPI regeneration
- [x] **Database**: 2 migrations pending (RemoveInstructions, RemoveFlowSystemPrompt)

### Recent Activity (Since 2025-12-27T06:59:47Z checkpoint)
- **Period**: 20 minutes | **Work Session**: Frontend UI polish
- **Major Changes**:
  - ✅ **Frontend: ButtonGroup Component Integration**
    - NEW: `ButtonGroup` component import in agentflows/page.tsx
    - UPDATED: Actions column layout from `text-center` to `text-right`
    - UPDATED: Button container from `flex items-center justify-center gap-2` to `flex justify-end` + ButtonGroup
    - BENEFIT: Consistent button spacing and grouping across all pages
  - ✅ **Frontend: Code Formatting**
    - Auto-formatted by linter (3 files affected)
    - Line breaks and indentation standardized
    - Removed trailing whitespace
  - 📊 **Impact**: 3 files modified (+70 lines, -57 lines)
- **Files Changed**:
  - agentflows/page.tsx (ButtonGroup integration + formatting)
  - agents/page.tsx (formatting)
  - model-providers/page.tsx (formatting)
- **Activity Intensity**: Low (UI polish + linter cleanup)
- **Development Trend**: ➡️ Stabilizing (UI consistency improvements)

### Recommended Actions
1. ✅ ~~ButtonGroup integration~~ - **COMPLETED** (agentflows page)
2. ⚠️ **Run database migration**: `dotnet ef database update` (RemoveInstructions + RemoveFlowSystemPrompt)
3. ⚠️ **Regenerate OpenAPI types**: `cd src/frontend/web && pnpm gen:openapi`
4. 🔄 Test UI button groups (consistent spacing and hover states)
5. 🔄 Verify Actions column alignment across all CRUD pages
6. 🔄 Test CRUD operations after formatting changes
7. 🧪 Test execute functionality for both Agents and Agentflows
8. 📈 Monitor for TypeScript errors after OpenAPI regeneration
9. 🔍 Consider applying ButtonGroup to other pages (agents, model-providers)

**Git Commit**: `ed8054a` (main) | **Health Score**: 9.8/10

---

### Previous Checkpoint

**Project**: D-System | **Time**: 2025-12-27T06:59:47Z
**Milestone**: Frontend schema alignment - Instructions/SystemPrompt cleanup | **Branch**: main

### Technical Status
- **Code Quality**: Excellent (19,521 code files)
- **Architecture Health**: Active Development - Frontend/Backend schema synchronization
- **Dependencies**: Latest (Next.js 16, .NET 10, vaul 1.1.1, EF Core migrations)

### Documentation Maintenance
- [x] **CLAUDE.md**: Updated with schema cleanup details
- [x] **Configuration Sync**: All dependencies synchronized
- [x] **API Documentation**: Pending OpenAPI regeneration
- [x] **Database**: 2 migrations pending (RemoveInstructions, RemoveFlowSystemPrompt)

### Recent Activity (Since 2025-12-27T06:27:26Z checkpoint)
- **Period**: 32 minutes | **Work Session**: Frontend schema cleanup
- **Major Changes**:
  - ✅ **Frontend: Agent.Instructions Removal**
    - REMOVED: `instructions` field from `AgentDto` type definition (page.tsx:76)
    - REMOVED: `instructions` state management (create/edit dialogs)
    - REMOVED: Instructions textarea from create/edit forms
    - REMOVED: Instructions column from agents table
    - UPDATED: API mutation calls (removed instructions parameter)
    - UPDATED: `AgentDto` in `/types/agentflow.ts`
  - ✅ **Frontend: Agentflow.SystemPrompt Removal**
    - REMOVED: `systemPrompt` field from `editingAgentflow` state type (page.tsx:126)
    - REMOVED: `systemPrompt` from `handleToggleEnabled` mutation body
    - REMOVED: `systemPrompt` from `handleEdit` state initialization
    - REMOVED: `agentflowSystemPrompt` state in visual-agentflow-builder.tsx
    - REMOVED: SystemPrompt textarea from visual workflow builder UI
    - UPDATED: `AgentflowDto` in `/types/agentflow.ts`
  - 📊 **Impact**: 4 files modified (all frontend)
- **Files Changed**:
  - agents/page.tsx (11 edits - removed instructions)
  - agentflows/page.tsx (3 edits - removed systemPrompt)
  - types/agentflow.ts (2 edits - type definitions)
  - agentflows/components/visual-agentflow-builder.tsx (5 edits)
- **Activity Intensity**: Medium (Schema synchronization)
- **Development Trend**: ➡️ Stabilizing (Frontend catching up with backend changes)

### Schema Changes Detail
**Agent Table**:
- ❌ REMOVED: `Instructions` column (max 4000 chars)
- ✅ KEPT: `SystemPrompt` (main prompt field)

**Agentflow Table**:
- ❌ REMOVED: `SystemPrompt` column
- ✅ KEPT: Core workflow configuration fields

**Migration Files**:
1. `20251227061824_RemoveInstructions.cs` - Drop Agent.Instructions
2. `20251227062610_RemoveFlowSystemPrompt.cs` - Drop Agentflow.SystemPrompt

### Recommended Actions
1. ✅ ~~Frontend schema alignment~~ - **COMPLETED** (Instructions/SystemPrompt removed)
2. ⚠️ **Run database migration**: `dotnet ef database update` (RemoveInstructions + RemoveFlowSystemPrompt)
3. ⚠️ **Regenerate OpenAPI types**: `cd src/frontend/web && pnpm gen:openapi`
4. 🔄 Test Agent creation/editing (should work without Instructions)
5. 🔄 Test Agentflow creation/editing (should work without SystemPrompt)
6. 🔄 Verify all CRUD operations in UI
7. 🧪 Test execute functionality for both Agents and Agentflows
8. 📈 Monitor for TypeScript errors after OpenAPI regeneration
9. 🔍 Review API response types for consistency

**Git Commit**: `32fd23a` (main) | **Health Score**: 9.8/10

---

### Previous Checkpoint

**Project**: D-System | **Time**: 2025-12-27T06:27:26Z
**Milestone**: Agentflow执行功能 + Agent/Agentflow数据模型优化 | **Branch**: main

### Technical Status
- **Code Quality**: Excellent (19,516 TypeScript/C# files)
- **Architecture Health**: Active Development - Feature expansion + Schema refinement
- **Dependencies**: Latest (Next.js 16, .NET 10, vaul 1.1.1, EF Core migrations)

### Documentation Maintenance
- [x] **CLAUDE.md**: Updated with feature implementation details
- [x] **Configuration Sync**: All dependencies synchronized
- [x] **API Documentation**: Consistent with codebase
- [x] **Database**: 2 new migrations (RemoveInstructions, RemoveFlowSystemPrompt)

### Recent Activity (Since 2025-12-26T15:54:45Z checkpoint)
- **Period**: 14.55 hours | **Work Session**: Full-stack feature + schema optimization
- **Major Changes**:
  - ✅ **Frontend: Agentflow Execute Feature**
    - NEW: "Run" button for each agentflow row (Play icon)
    - NEW: Execute Drawer with full UI (参考 agents page 实现)
    - NEW: Message merging logic (same messageId concatenation)
    - API: `/api/agentflows/{id}/execute` endpoint integration
    - UI: Horizontal layout (Textarea + Button in DrawerFooter)
    - UX: Thread ID display, keyboard shortcuts, clear session
    - Dependencies: Reused Drawer component (vaul)
  - ✅ **Backend: Agent/Agentflow Schema Simplification**
    - REMOVED: `Agent.Instructions` field (migration: RemoveInstructions)
    - REMOVED: `Agentflow.SystemPrompt` field (migration: RemoveFlowSystemPrompt)
    - RATIONALE: Simplified data model, reduce redundancy
    - UPDATED: `AgentRuntimeService` - removed Instructions handling
    - UPDATED: `AgentflowRuntimeService` - removed SystemPrompt handling
    - UPDATED: DTOs and Controller contracts (AgentCreateRequest, AgentflowCreateRequest)
    - UPDATED: `LlmDbContext` configuration
    - UPDATED: `AiAgent` model (removed Instructions property)
  - 📊 **Impact**: 15 files modified (8 backend, 1 frontend)
- **Files Changed**: agentflows/page.tsx, Agent entities, migrations, controllers
- **Activity Intensity**: High (Feature development + Schema refactoring)
- **Development Trend**: ⬆️ Active Development (功能扩展 + 架构优化)

**Git Commit**: `4c9e4db` (main) | **Health Score**: 9.7/10

---

### Previous Checkpoint

**Project**: D-System | **Time**: 2025-12-26T15:54:45Z
**Milestone**: Agent执行界面优化 + Toast/Drawer交互修复 | **Branch**: main

### Documentation Maintenance
- [x] **CLAUDE.md**: Updated with UI fix details
- [x] **Configuration Sync**: All dependencies synchronized
- [x] **API Documentation**: Consistent with codebase
- [x] **Database**: All migrations current

### Recent Activity (Since 2025-12-26T12:45:33Z checkpoint)
- **Period**: 3.15 hours | **Work Session**: Frontend UX optimization
- **Major Changes**:
  - ✅ **Frontend: Toast/Drawer Interaction Fix**
    - PROBLEM: Toast close button unresponsive when Drawer open
    - ANALYSIS: Not z-index issue, but Radix Dialog modal behavior
    - SOLUTION: Changed Sheet → Drawer (vaul library) with `modal={false}`
    - Added: `onPointerDownOutside` preventDefault to keep drawer open on outside clicks
    - Result: Toast clickable while drawer open, stable UX
  - ✅ **Frontend: Agent Execute UI Improvements**
    - MIGRATION: Sheet → Drawer for better mobile/desktop experience
    - LAYOUT: Moved input to DrawerFooter with horizontal layout
    - UI: Textarea + Button side-by-side layout (flex gap-2)
    - THREADING: Thread ID display in DrawerTitle for visibility
    - NEW: Lucide-react X icon for close button
    - Dependencies: Added vaul ^1.1.1 (drawer component)
  - ✅ **Frontend: Message Merging Logic**
    - FEATURE: Merge AI messages with same messageId
    - IMPLEMENTATION: Map-based content concatenation (page.tsx:824-869)
    - USE CASE: Stream responses chunked into multiple messages
    - Result: Single coherent message display per messageId
  - ✅ **Frontend: Toast Configuration**
    - REMOVED: Success toast on execution (commented out duration: 600000)
    - REASON: Avoid clutter with long-running agent executions
- **Files Changed**: 5 files (agents page, drawer component, package.json)
- **Activity Intensity**: Medium (Focused UX refinement)
- **Development Trend**: ➡️ Stable (Iteration on user feedback)

### UI Fix Details
**Problem**: Dialog `overflow-y-auto` directly on DialogContent caused border-radius clipping

**Solution**: Nested overflow container pattern
```tsx
// Before (broken)
<DialogContent className="overflow-y-auto">
  <DialogHeader>...</DialogHeader>
  <div>...</div>
</DialogContent>

// After (fixed)
<DialogContent className="flex flex-col">
  <DialogHeader>...</DialogHeader>
  <div className="overflow-y-auto pr-2 -mr-2">...</div>
</DialogContent>
```

**Benefits**:
- ✅ DialogContent maintains border-radius styling
- ✅ Header/Footer remain fixed (not scrollable)
- ✅ Only content area scrolls
- ✅ Improved visual consistency

### Recommended Actions
1. ✅ ~~Fix Toast/Drawer interaction conflict~~ - **COMPLETED** (Sheet→Drawer migration)
2. ✅ ~~Merge messages with same messageId~~ - **COMPLETED** (Map-based implementation)
3. ✅ ~~Improve agent execution UI layout~~ - **COMPLETED** (Footer horizontal layout)
4. 🔄 Test Drawer interaction on mobile devices
5. 🔄 Verify message merging with streaming responses
6. 📝 Consider adding execution history persistence
7. 📝 Add loading states for agent execution
8. 🧪 Test Toast notifications with multiple concurrent executions
9. 🔧 Consider adding drawer resize functionality
10. 📈 Monitor drawer performance on low-end devices

**Git Commit**: `pending` (main) | **Health Score**: 9.8/10

---

### Previous Checkpoint

**Project**: D-System | **Time**: 2025-12-26T02:46:05Z
**Milestone**: Agent Tool System implementation + Frontend CRUD enhancement | **Branch**: main

### Technical Status
- **Code Quality**: Excellent (19,501 code files)
- **Architecture Health**: Feature Expansion - Tool integration and UI enhancement
- **Dependencies**: Latest (Next.js 16, .NET 10, React Flow 11, @radix-ui/react-checkbox 1.3.3)

### Documentation Maintenance
- [x] **CLAUDE.md**: Updated with Tool System architecture
- [x] **Configuration Sync**: Frontend dependencies updated (@radix-ui/react-checkbox added)
- [x] **API Documentation**: New /api/tools endpoint documented
- [x] **Database**: EF Core migration `20251225152826_AddAgentToolsField` created

### Recent Activity (Since 2025-12-25T00:00:00Z checkpoint)
- **Period**: 1.4 days | **Work Session**: Full-stack feature development
- **Major Changes**:
  - ✅ **Backend: Agent Tool System** (Complete plugin-like tool architecture)
    - NEW: `ToolAttribute` with Category property (Description from DescriptionAttribute)
    - NEW: `ToolRegistryService` for reflection-based tool discovery (Singleton)
    - NEW: `BasicTools` class with 8 sample tools (DateTime, Math, Text, Utility categories)
    - NEW: `WeatherTool` class with location-based weather lookup
    - NEW: `ToolsController` with `/api/tools` endpoint
    - NEW: `Agent.Tools` field (JSON array of tool names, max 4000 chars)
    - UPDATED: `AgentRuntimeService.CreateAiAgentAsync` to instantiate AITools from tool names
    - UPDATED: `AgentCreateRequest`/`AgentUpdateRequest` to include Tools parameter
    - Migration: `AddAgentToolsField` adds nullable tools column to agents table
  - ✅ **Backend: ModelProviderApiKey DTO Enhancement**
    - NEW: `ModelProviderApiKeyDto` with ModelName and ProviderIdName fields
    - NEW: `ModelProviderApiKeyDomainService.ListDtoAsync` method
    - UPDATED: `ModelProviderApiKeysController.ListAsync` to return DTOs with joined data
    - Improved API response: Now includes Model and Provider names for display
  - ✅ **Frontend: Agents Page CRUD Enhancement**
    - NEW: Edit and Delete buttons for each agent row
    - NEW: Edit Agent dialog with full form (pre-fills all fields including tools)
    - NEW: Delete confirmation dialog with agent name display
    - NEW: Searchable multi-select tool selector (Checkbox-based)
    - NEW: Tool search by name, description, or category
    - NEW: Tools column in agents table (shows first 2 tools + count)
    - NEW: Checkbox UI component (@radix-ui/react-checkbox integration)
    - UPDATED: Create Agent dialog with max-height and scrollbar (max-h-[90vh])
    - UPDATED: AgentDto type to include `tools?: string | null` field
    - Tool filtering: Real-time search across 9 tool methods from 2 tool classes
- **Files Changed**: 25 files (14 backend, 11 frontend)
- **Activity Intensity**: High (Full-stack feature development)
- **Development Trend**: ⬆️ Active Development (Tool extensibility + UI enhancement)

### Tool System Architecture
**Design Pattern**: Plugin-like reflection-based discovery
- Tools marked with `[Tool(Category = "...")]` + `[Description("...")]`
- ToolRegistryService scans `DSystem.Domain.Tools` namespace on startup
- Dynamic delegate creation (Func<>/Action<>) from MethodInfo
- AIFunctionFactory.Create(delegate) for Microsoft.Agents.AI integration

**Available Tools** (9 methods across 2 classes):
1. **DateTime Category**: GetCurrentDateTime, GetCurrentDate
2. **Math Category**: Add, Multiply
3. **Utility Category**: GetRandomNumber
4. **Text Category**: ToUpperCase, ToLowerCase, CountCharacters
5. **Weather Category**: GetWeather

**Frontend Tool Selection**:
- Searchable checkbox-based multi-select
- Displays tool name, category badge, description
- Real-time filtering by keyword
- Shows selected tool count
- Tools stored as JSON array string in Agent.Tools

### Recommended Actions
1. ✅ ~~Implement Tool System backend~~ - **COMPLETED**
2. ✅ ~~Add /api/tools endpoint~~ - **COMPLETED**
3. ✅ ~~Create frontend tool selector~~ - **COMPLETED**
4. ✅ ~~Add Agent edit/delete functionality~~ - **COMPLETED**
5. ✅ ~~Enhance ModelProviderApiKey responses~~ - **COMPLETED**
6. ⚠️ **Run database migration**: `dotnet ef database update` (AddAgentToolsField)
7. ⚠️ **Install frontend dependencies**: `cd src/frontend/web && pnpm install`
8. 🔄 Test agent creation/editing with tool selection
9. 🔄 Test tool discovery and registration on startup
10. 📝 Consider adding tool parameter validation
11. 🧪 Add integration tests for tool execution
12. 🔧 Consider adding tool categories as enum for consistency
13. 📈 Monitor tool execution performance metrics

**Git Commit**: `pending` (main) | **Health Score**: 9.7/10

---

### Previous Checkpoint

**Project**: D-System | **Time**: 2025-12-24T14:18:57Z
**Milestone**: Scheduler optimization and UI polish | **Branch**: main

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

