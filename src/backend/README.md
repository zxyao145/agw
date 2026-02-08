# Project dependencies



```mermaid
flowchart BT

%% ========= Nodes (shared foundations) =========
infra[Infrastructure]
shared[Shared]

%% ========= API Layer =========
subgraph API["API"]
  direction LR
  api[API]
  mgr[Manager.API]
end

%% ========= A2A Gateway =========
subgraph A2ABox["A2A Protocol"]
  direction TB
  a2a[A2A]
end

%% ========= Core =========
subgraph Core["Core"]
  direction TB
  dom[Domain]
  app[Application]
  dom --> app
end

%% ========= External =========
subgraph External["External"]
  direction TB
  extAgents[External Agents]
end

%% ========= Session Records =========
subgraph SessionRecords["Session Records"]
  direction TB
  sessDom[Domain]
  sessApp[Application]
  sessDom --> sessApp
end

%% ========= Relationships =========

%% A2A talks to API (integration / boundary dependency)
a2a -.-> api
a2a -.-> mgr

%% Core uses A2A
app -.-> a2a

%% External agents connect via A2A
extAgents -.-> a2a

%% Infrastructure dependencies
app -.-> infra
sessApp -.-> infra

%% Shared components reused by modules
shared -.-> SessionRecords
shared -.-> External

%% SessionRecords supports Core and External
SessionRecords -.-> Core
SessionRecords -.-> External

```



# LLM Manager Backend

ASP.NET Core + EF Core backend for managing LLM models, providers, their pricing links, and API keys. Clean Architecture with anemic domain models operated through domain services, a generic repository, and a unit of work.

## Projects
- `LlmManager.Domain`: Entities, enums, repository/UoW abstractions, and domain services.
- `LlmManager.Infrastructure`: EF Core DbContext plus repository and unit of work implementations; supports SQLite (default), PostgreSQL, and MySQL.
- `LlmManager.Api`: Web API that wires up DI and exposes CRUD/list endpoints.

## Configuration
`appsettings.json` (or environment vars) drives database selection:
```json
"Database": {
  "Provider": "sqlite", // sqlite | postgres | mysql
  "ConnectionString": "Data Source=llmmanager.db"
}
```

## Run
```bash
cd src/backend/LlmManager.Api
dotnet run
```
Swagger/OpenAPI available in Development at `/openapi`.

## EF Core migrations
Generate migrations from the API project root:
```bash
dotnet ef migrations add InitialCreate -p ../LlmManager.Infrastructure -s .
dotnet ef database update -p ../LlmManager.Infrastructure -s .
```
Swap `Database:Provider`/`ConnectionString` to target PostgreSQL or MySQL before applying migrations.
