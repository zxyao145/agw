# Agw.Providers DDD Structure

This document describes the current DDD-oriented structure for `src/backend/Agw.Providers` after the service-layer refactor.

## Refactor Constraints

- Keep the current anemic entity model.
- Do not move behavior into entities.
- Distinguish application services from domain services.
- Keep persistence and transaction concerns outside the domain service layer.

## Current Responsibility Split

- `Application/`
  - Application service interfaces and implementations.
  - Owns use-case orchestration, repository access, transaction commit, and request-to-entity assembly.
- `DomainServices/`
  - Owns domain rules that can operate on anemic entities.
  - Sets audit fields, ids, and internal consistency for entity state changes.
  - Does not own repository queries or `IUnitOfWork`.
- `Entities/`
  - Pure state containers for the Providers bounded context.
  - Remain anemic by design.
- `Contracts/Manager/`
  - HTTP request contracts for the management API.
- `Controllers/Controllers/`
  - Presentation layer.
  - Accepts HTTP requests and delegates to application service interfaces.

## Current Physical Layout

```text
src/backend/Agw.Providers/
  Application/
    IModelAppService.cs
    IProviderAppService.cs
    IModelProviderAppService.cs
    ModelAppService.cs
    ProviderAppService.cs
    ModelProviderAppService.cs
  Contracts/Manager/
    ModelRequests.cs
    ProviderRequests.cs
    ModelProviderRequests.cs
  Controllers/Controllers/
    ModelsController.cs
    ProvidersController.cs
    ModelProvidersController.cs
  DomainServices/
    ModelDomainService.cs
    ProviderDomainService.cs
    ModelProviderDomainService.cs
  Entities/
    LlmModel.cs
    Provider.cs
    ProviderAuthConfig.cs
    ModelProvider.cs
```

## Request Flow

```text
Controller
  -> Application service interface
  -> Application service implementation
  -> Domain service
  -> Repository / UnitOfWork
  -> Persistence
```

## Boundary Definitions

### Application Layer

Application services coordinate a complete use case:

- Load aggregates or entity graphs from repositories.
- Call domain services to prepare or apply state changes.
- Persist changes and commit the transaction.
- Translate manager API requests into domain entities.

This is the correct place for CRUD orchestration while the model stays anemic.

### Domain Layer

Because entities must stay anemic, the domain layer expresses business-side state transition rules through domain services instead of entity methods.

Examples in this module:

- Assigning ids and audit fields during create.
- Applying audit fields during update.
- Normalizing provider auth config ownership and timestamps.

### Infrastructure Boundary

`Agw.Providers` should not absorb EF Core persistence details beyond repository usage in application services. EF mappings, concrete repositories, and database concerns remain in `Agw.Infrastructure`.

## Recommended Next Step

If the module keeps growing, split the application layer further by use case:

```text
Application/
  Models/
  Providers/
  ModelProviders/
```

That keeps each subdomain use-case surface small without changing the current domain model choice.
