# Integrations Table Comments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add database-level descriptions to the six plugin/connection tables introduced by the Integrations refactor.

**Architecture:** Store each comment in the owning EF Core entity configuration through `ToTable(table => table.HasComment(...))`. Keep the existing unapplied migration, designer, and model snapshot synchronized so PostgreSQL creates the comments without adding another migration.

**Tech Stack:** .NET 10, EF Core 10 relational metadata and migrations, xUnit.

## Global Constraints

- Only the six plugin/connection tables are in scope; do not add column comments or touch legacy tables.
- Use concise English comments.
- Do not apply the migration.
- Do not stage, commit, push, or create a PR.

---

### Task 1: Add and persist Integrations table comments

**Files:**
- Create: `tests/Agw.Integrations.Tests/IntegrationTableCommentTests.cs`
- Modify: `src/server/Agw.Data/Entities/Integrations/PluginInstallationConfiguration.cs`
- Modify: `src/server/Agw.Data/Entities/Integrations/PluginInstallationCredentialConfiguration.cs`
- Modify: `src/server/Agw.Data/Entities/Integrations/ConnectionConfiguration.cs`
- Modify: `src/server/Agw.Data/Entities/Integrations/ConnectionCredentialConfiguration.cs`
- Modify: `src/server/Agw.Data/Entities/Agents/AgentConnectionRelationConfiguration.cs`
- Modify: `src/server/Agw.Data/Entities/Projects/ProjectConnectionRelationConfiguration.cs`
- Modify: `src/server/Agw.Infrastructure/Migrations/20260715074414_RefactorIntegrationsToPluginConnections.cs`
- Modify: `src/server/Agw.Infrastructure/Migrations/20260715074414_RefactorIntegrationsToPluginConnections.Designer.cs`
- Modify: `src/server/Agw.Infrastructure/Migrations/LlmDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: EF Core `EntityTypeBuilder<TEntity>.ToTable(Action<TableBuilder<TEntity>>)` with `TableBuilder<TEntity>.HasComment(string)`, and relational `IReadOnlyEntityType.GetComment()` metadata.
- Produces: Six stable table comments represented consistently by the runtime model and migration artifacts.

- [ ] **Step 1: Write the failing model metadata test**

Create `IntegrationTableCommentTests.cs` with one theory-like data set covering these exact mappings:

```csharp
(typeof(PluginInstallation), "Stores platform-wide plugin installation configuration."),
(typeof(PluginInstallationCredential), "Stores protected credentials owned by a plugin installation."),
(typeof(Connection), "Represents an external account or service endpoint available to agents."),
(typeof(ConnectionCredential), "Stores protected credentials owned by an integration connection."),
(typeof(AgentConnectionRelation), "Binds an agent to an integration connection."),
(typeof(ProjectConnectionRelation), "Binds a project to an integration connection.")
```

Build an SQLite `AgwDbContext`, look up each entity type with `dbContext.Model.FindEntityType`, and assert `entityType.GetComment()` equals the expected text.

- [ ] **Step 2: Run the focused test and verify Red**

Run:

```bash
dotnet test tests/Agw.Integrations.Tests/Agw.Integrations.Tests.csproj \
  --filter "FullyQualifiedName~IntegrationTableCommentTests" \
  --no-restore --nologo
```

Expected: FAIL because every current entity comment is null.

- [ ] **Step 3: Add the six model comments**

At the start of each owning `Configure` method, add the corresponding call:

```csharp
// PluginInstallationConfiguration
builder.ToTable(table => table.HasComment("Stores platform-wide plugin installation configuration."));

// PluginInstallationCredentialConfiguration
builder.ToTable(table => table.HasComment("Stores protected credentials owned by a plugin installation."));

// ConnectionConfiguration
builder.ToTable(table => table.HasComment("Represents an external account or service endpoint available to agents."));

// ConnectionCredentialConfiguration
builder.ToTable(table => table.HasComment("Stores protected credentials owned by an integration connection."));

// AgentConnectionRelationConfiguration
builder.ToTable(table => table.HasComment("Binds an agent to an integration connection."));

// ProjectConnectionRelationConfiguration
builder.ToTable(table => table.HasComment("Binds a project to an integration connection."));
```

- [ ] **Step 4: Synchronize the unapplied migration artifacts**

For each of the six `CreateTable` calls in `RefactorIntegrationsToPluginConnections`, add the matching `comment:` argument. In the migration designer and model snapshot, add the same `b.HasComment("...")` annotation to the corresponding entity block. Do not add comments to tables recreated by `Down`.

- [ ] **Step 5: Verify Green and migration structure**

Run the focused test again and expect PASS. Then run:

```bash
dotnet test tests/Agw.Integrations.Tests/Agw.Integrations.Tests.csproj --no-restore --nologo
dotnet ef migrations script 0 RefactorIntegrationsToPluginConnections \
  -p src/server/Agw.Infrastructure \
  -s src/server/Agw.Host \
  --no-build
```

Confirm the Integrations test project passes and the migration can generate without being applied. If local configuration selects SQLite, also inspect the model artifacts directly for all six comment strings because SQLite does not emit native table comments.

- [ ] **Step 6: Run final regression and diff checks**

Run:

```bash
dotnet test Agw.slnx --no-restore --nologo
git diff --check
git diff --cached --quiet
```

Expected: all tests pass, no whitespace errors, and the staging area remains empty.
