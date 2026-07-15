# Integrations Table Comments Design

## Scope

Add database-level table comments to the six tables introduced by the Integrations plugin/connection refactor:

- `plugin_installation`
- `plugin_installation_credential`
- `integration_connection`
- `integration_connection_credential`
- `agent_connection_relation`
- `project_connection_relation`

Column comments and legacy tables are out of scope.

## Design

Configure concise English comments in each entity's EF Core configuration with `ToTable(table => table.HasComment(...))` so the comments are part of the relational model without using the obsolete entity-level API. Synchronize the existing, unapplied `RefactorIntegrationsToPluginConnections` migration, its designer, and the model snapshot rather than creating another migration.

PostgreSQL persists the comments as database metadata. SQLite does not natively persist table comments, but the EF Core relational model retains the annotations.

## Verification

Add model metadata tests for all six table comments, then verify the migration/snapshot structure and run the relevant test projects. Do not apply the migration or perform Git staging, commits, pushes, or PR operations.
