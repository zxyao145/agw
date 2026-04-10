# Agw.Integrations

`Agw.Integrations` is responsible for maintaining directory definitions for external application integrations, OAuth callback endpoints, and domain entities related to runtime integration configurations.

## Module Boundaries

- `AppDefinition` is a static directory definition and is not stored in the database.
- The primary key of `AppDefinition` is `Name`, and its data source is fixed as `IntegrationConstants.AppList`.
- `AppInstance` is the specific instantiated configuration of an `AppDefinition` and is persisted in the database.
- `OAuthAuthorizationToken` represents the authorization result of an `AppInstance` and has a one-to-one relationship with `AppInstance`.

## Data Model Constraints

### AppDefinition

- Used solely for UI display, tool integration, and default OAuth metadata descriptions.
- Do not declare `DbSet<AppDefinition>` in `AgwDbContext`, nor create a corresponding database table.
- Retrieval is uniformly handled through `IRepository<AppDefinition>`, implemented as `AppDefinitionRepo`, which directly reads from `IntegrationConstants.AppList` at the underlying level.

### AppInstance

- `Id` is the primary key.
- `AppName` corresponds to `AppDefinition.Name`.
- `AppName` requires a regular index, but not a unique index.
- `ClientId` requires a unique index to ensure that the same OAuth client configuration is stored only once.
- A single `AppDefinition` can correspond to multiple `AppInstance` instances.

### OAuthAuthorizationToken

- `AppInstanceId` is a foreign key referencing `AppInstance.Id`.
- `AppInstanceId` requires a unique index to ensure that each `AppInstance` is associated with at most one authorization record.
- When the same `AppInstance` is reauthorized, the existing token should be overwritten rather than creating a second token.

## Current Implementation Conventions

- `IntegrationConstants.AppList` is the single source of truth for the application definition directory.
- `AppDefinitionRepo` in `Agw.Infrastructure` is a read-only repository; any attempts to add, update, or delete entries will throw a `NotSupportedException`.
- The OAuth callback process locates the target instance using the `AppInstanceId` in the configuration and writes the authorization result to the corresponding `OAuthAuthorizationToken`.

## Development Considerations

- When adding a new application type, modify `IntegrationConstants.AppList` directly.
- When adding `AppInstance` or `OAuthAuthorizationToken` fields, you must synchronously update the EF Core configuration in `AgwDbContext`.
- Do not add migration tables for `AppDefinition`; it is a static in-code directory, not a persistent entity.
