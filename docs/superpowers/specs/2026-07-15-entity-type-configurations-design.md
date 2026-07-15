# Entity Type Configurations Design

## Goal

Move every persisted entity's EF Core Fluent API mapping out of
`AgwDbContext.OnModelCreating` and into an entity-specific
`IEntityTypeConfiguration<T>` class, following the existing
`ProjectConfiguration` pattern without changing the database model.

## Scope

- Keep the existing `ProjectConfiguration` as the reference implementation.
- Add configurations for the other 25 entity types represented by `DbSet`
  properties in `AgwDbContext`.
- Store each configuration beside its entity and use the entity type name plus
  the `Configuration` suffix.
- Add `EntityTypeConfigurationAttribute` to every persisted entity so EF Core
  discovers its configuration when the entity enters the model.
- Remove the migrated Fluent API blocks and now-unused imports from
  `AgwDbContext`.
- Give `Agw.Data` a direct `Microsoft.EntityFrameworkCore.Relational`
  dependency because relocated mappings use relational APIs such as
  `HasColumnType` and `HasDatabaseName`.
- Preserve `DbSet` properties, `OnConfiguring`, save hooks, row-version
  stamping, and relation-pruning behavior.

`AppDefinition` is excluded because it is a static integration catalog entry,
not a persisted entity. No migration will be created or applied.

## Configuration Groups

- Providers: `Provider`, `ProviderAuthConfig`, `LlmModel`, and
  `ModelProviderRelation`.
- Agents and tools: `Agent`, `AgentAppRelation`, `AgentSkillRelation`,
  `McpServer`, and `AgentMcpServerRelation`.
- Agentflows and observability: `Agentflow`, `AgentflowNode`, `AgentflowEdge`,
  `AgentflowTrace`, and `AgentUsage`.
- Projects and tasks: `ProjectSkillRelation`, `ProjectMcpServerRelation`,
  `ProjectAppRelation`, `ProjectContext`, `TaskSessionBinding`, and
  `TaskRecord`.
- Jobs: `Job` and `JobLog`.
- Integrations: `AppInstance` and `OAuthAuthorizationToken`.
- Skills: `Skill`.

Each configuration will copy the corresponding existing Fluent API mapping
exactly, including keys, indexes, maximum lengths, required flags, enum and
JSON conversions, relational delete behavior, concurrency metadata, column
types, defaults, and database index names.

## Discovery and Data Flow

`AgwDbContext` continues to expose the same `DbSet` properties. When EF Core
discovers an entity, its `EntityTypeConfigurationAttribute` points to the
matching configuration class, which applies the mapping previously applied by
`OnModelCreating`. Runtime repository and save behavior therefore remain
unchanged.

## Verification

1. Add a structural test that enumerates every `[Table]` entity in the
   `Agw.Data` assembly and requires exactly one valid
   `EntityTypeConfigurationAttribute` whose configuration implements
   `IEntityTypeConfiguration<TEntity>`. Run it before implementation and
   confirm it fails for the unmigrated entities.
2. Add the configuration classes and attributes, then rerun the structural
   test until it passes.
3. Run the existing EF model tests to cover property constraints, indexes,
   relationships, conversions, and persistence behavior.
4. Run the relevant backend test project and a repository build.
5. Inspect the final diff to ensure `AgwDbContext` retains only context-level
   behavior and that no migration or unrelated file was changed.

## Acceptance Criteria

- All 26 persisted `[Table]` entities, including `Project`, use an
  entity-specific configuration through `EntityTypeConfigurationAttribute`.
- `AgwDbContext.OnModelCreating` no longer contains entity mappings.
- The EF Core model and runtime persistence behavior remain unchanged.
- `AppDefinition` remains outside the EF model.
- No EF Core migration is created or applied.
- Existing user changes are preserved, and no Git commit is created without
  explicit authorization.
