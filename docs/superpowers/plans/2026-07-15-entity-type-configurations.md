# Entity Type Configurations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move all remaining EF Core entity mappings from `AgwDbContext.OnModelCreating` into entity-specific configuration classes discovered through `EntityTypeConfigurationAttribute`.

**Architecture:** Keep each persisted entity and its `IEntityTypeConfiguration<TEntity>` in the same module folder and namespace. EF Core discovers the configuration through an attribute on the entity; `AgwDbContext` retains only context-level concerns such as `DbSet` access, migration differ replacement, save hooks, row-version stamping, and manual relation pruning.

**Tech Stack:** .NET 10, C# 14, Entity Framework Core 10, xUnit v3, SQLite test provider.

## Global Constraints

- Preserve the current EF model exactly: keys, indexes, conversions, relationships, delete behavior, maximum lengths, column types, defaults, concurrency metadata, and database names.
- Do not create or apply an EF Core migration.
- Do not configure `AppDefinition`; it is not persisted.
- Do not use assembly scanning; every entity must declare its configuration with `EntityTypeConfigurationAttribute`.
- Keep EF Core and EF Core Relational at the same centrally managed `10.0.9` version.
- Do not overwrite the user's staged `Project`, `ProjectConfiguration`, `Agw.Data.csproj`, or `AgwDbContext` work.
- Preserve the user's unstaged `TaskRecord.ProjectContext` relocation and formatting; add only the required import and configuration attribute to that entity.
- Do not create a Git commit or stage additional files without explicit user authorization.

---

### Task 1: Add structural configuration coverage tests

**Files:**

- Create: `tests/Agw.Projects.Tests/Infrastructure/EntityTypeConfigurationTests.cs`

**Interfaces:**

- Consumes: `[Table]`, `EntityTypeConfigurationAttribute`, and `IEntityTypeConfiguration<TEntity>` metadata.
- Produces: one group-level assertion per migration batch plus a whole-assembly guard covering all 26 persisted entities.

- [ ] **Step 1: Create the structural test file**

```csharp
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;

using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tests;

public class EntityTypeConfigurationTests
{
    [Fact]
    public void ProviderAndSkillEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Provider),
            typeof(ProviderAuthConfig),
            typeof(LlmModel),
            typeof(ModelProviderRelation),
            typeof(Skill));
    }

    [Fact]
    public void AgentAndToolEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Agent),
            typeof(AgentAppRelation),
            typeof(AgentSkillRelation),
            typeof(McpServer),
            typeof(AgentMcpServerRelation));
    }

    [Fact]
    public void AgentflowAndObservableEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Agentflow),
            typeof(AgentflowNode),
            typeof(AgentflowEdge),
            typeof(AgentflowTrace),
            typeof(AgentUsage));
    }

    [Fact]
    public void ProjectAndTaskEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Project),
            typeof(ProjectSkillRelation),
            typeof(ProjectMcpServerRelation),
            typeof(ProjectAppRelation),
            typeof(ProjectContext),
            typeof(TaskSessionBinding),
            typeof(TaskRecord));
    }

    [Fact]
    public void JobAndIntegrationEntities_DeclareMatchingConfigurations()
    {
        AssertConfigured(
            typeof(Job),
            typeof(JobLog),
            typeof(AppInstance),
            typeof(OAuthAuthorizationToken));
    }

    [Fact]
    public void PersistedEntities_AllDeclareMatchingConfigurations()
    {
        var entityTypes = typeof(Project).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<TableAttribute>() is not null)
            .OrderBy(type => type.FullName)
            .ToArray();

        Assert.Equal(26, entityTypes.Length);
        AssertConfigured(entityTypes);
    }

    private static void AssertConfigured(params Type[] entityTypes)
    {
        var failures = entityTypes
            .Where(entityType =>
            {
                var attribute = entityType.GetCustomAttribute<EntityTypeConfigurationAttribute>();
                if (attribute is null)
                {
                    return true;
                }

                var expectedInterface = typeof(IEntityTypeConfiguration<>).MakeGenericType(entityType);
                return !expectedInterface.IsAssignableFrom(attribute.EntityTypeConfigurationType);
            })
            .Select(entityType => entityType.FullName)
            .ToArray();

        Assert.Empty(failures);
    }
}
```

- [ ] **Step 2: Run the structural tests and verify RED**

Run:

```bash
dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~EntityTypeConfigurationTests"
```

Expected: FAIL. `Project` is the only configured entity, so every group test and the whole-assembly guard report unmigrated entity names.

---

### Task 2: Extract provider and skill configurations

**Files:**

- Modify: `src/server/Directory.Packages.props`
- Modify: `src/server/Agw.Data/Agw.Data.csproj`
- Create: `src/server/Agw.Data/Entities/Providers/ProviderConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Providers/ProviderAuthConfigConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Providers/LlmModelConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Providers/ModelProviderRelationConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Skills/SkillConfiguration.cs`
- Modify: the five matching entity files to add the EF Core import and attribute.

**Interfaces:**

- Consumes: mappings currently in `AgwDbContext.OnModelCreating` lines 79–126 and 169–176.
- Produces: five `IEntityTypeConfiguration<TEntity>` implementations discovered from the matching entities.

- [ ] **Step 1: Add the direct relational mapping dependency**

Add `<PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.9" />`
beside the existing EF Core versions in `src/server/Directory.Packages.props`.
Add `<PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />`
beside the existing EF Core reference in `Agw.Data.csproj`. This makes
`HasColumnType` and `HasDatabaseName` available where the relocated
configurations compile.

- [ ] **Step 2: Add the five entity attributes**

Add `using Microsoft.EntityFrameworkCore;` and place the listed attribute directly below `[Table(...)]`:

| Entity | Attribute |
|---|---|
| `Provider` | `[EntityTypeConfiguration(typeof(ProviderConfiguration))]` |
| `ProviderAuthConfig` | `[EntityTypeConfiguration(typeof(ProviderAuthConfigConfiguration))]` |
| `LlmModel` | `[EntityTypeConfiguration(typeof(LlmModelConfiguration))]` |
| `ModelProviderRelation` | `[EntityTypeConfiguration(typeof(ModelProviderRelationConfiguration))]` |
| `Skill` | `[EntityTypeConfiguration(typeof(SkillConfiguration))]` |

- [ ] **Step 3: Create the provider and skill configuration classes**

Each file imports `Microsoft.EntityFrameworkCore` and
`Microsoft.EntityFrameworkCore.Metadata.Builders`, uses the namespace shown
below, and declares the listed interface implementation with a public
`Configure(EntityTypeBuilder<TEntity> builder)` method:

| File | Namespace | Declaration |
|---|---|---|
| `ProviderConfiguration.cs` | `Agw.Shared.Data.Entities.Providers` | `ProviderConfiguration : IEntityTypeConfiguration<Provider>` |
| `ProviderAuthConfigConfiguration.cs` | `Agw.Shared.Data.Entities.Providers` | `ProviderAuthConfigConfiguration : IEntityTypeConfiguration<ProviderAuthConfig>` |
| `LlmModelConfiguration.cs` | `Agw.Shared.Data.Entities.Providers` | `LlmModelConfiguration : IEntityTypeConfiguration<LlmModel>` |
| `ModelProviderRelationConfiguration.cs` | `Agw.Shared.Data.Entities.Providers` | `ModelProviderRelationConfiguration : IEntityTypeConfiguration<ModelProviderRelation>` |
| `SkillConfiguration.cs` | `Agw.Shared.Data.Entities.Skills` | `SkillConfiguration : IEntityTypeConfiguration<Skill>` |

Use these exact `Configure` bodies:

```csharp
// ProviderConfiguration
builder.HasKey(e => e.Id);
builder.HasIndex(e => new { e.Name, e.ProviderType }).IsUnique();
builder.Property(e => e.Name).IsRequired().HasMaxLength(64);
builder.Property(e => e.Endpoint).IsRequired().HasMaxLength(500);
builder.Property(e => e.Description).HasMaxLength(1000);

// ProviderAuthConfigConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.AuthType).HasConversion<int>();
builder.Property(e => e.ApiKey).HasMaxLength(2000);
builder.Property(e => e.EnvName).HasMaxLength(200);
builder.HasOne(e => e.Provider)
    .WithMany(p => p.AuthConfigs)
    .HasForeignKey(e => e.ProviderId)
    .OnDelete(DeleteBehavior.Cascade);

// LlmModelConfiguration
builder.HasKey(e => e.Id);
builder.HasIndex(e => e.Name).IsUnique();
builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
builder.Property(e => e.Description).HasMaxLength(1000);

// ModelProviderRelationConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.InputPrice).HasColumnType("decimal(18,4)");
builder.Property(e => e.OutputPrice).HasColumnType("decimal(18,4)");
builder.Property(e => e.CacheRead).HasColumnType("decimal(18,4)");
builder.Property(e => e.CacheWrite).HasColumnType("decimal(18,4)");
builder.HasOne(e => e.Model)
    .WithMany(m => m.Providers)
    .HasForeignKey(e => e.ModelId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasOne(e => e.Provider)
    .WithMany(p => p.Models)
    .HasForeignKey(e => e.ProviderId)
    .OnDelete(DeleteBehavior.Cascade);

// SkillConfiguration
builder.HasKey(e => e.Id);
builder.HasIndex(e => e.Name).IsUnique();
builder.Property(e => e.Name).IsRequired().HasMaxLength(64);
builder.Property(e => e.Description).IsRequired().HasMaxLength(1024);
builder.Property(e => e.ContentPath).IsRequired().HasMaxLength(500);
```

- [ ] **Step 4: Run the provider/skill structural test and verify GREEN**

```bash
dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~ProviderAndSkillEntities_DeclareMatchingConfigurations"
```

Expected: PASS.

---

### Task 3: Extract agent and tool configurations

**Files:**

- Create: `src/server/Agw.Data/Entities/Agents/AgentConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Agents/AgentAppRelationConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Agents/AgentSkillRelationConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Agents/McpServerConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Agents/AgentMcpServerRelationConfiguration.cs`
- Modify: the five matching entity files to add the EF Core import and attribute.

**Interfaces:**

- Consumes: mappings currently in `AgwDbContext.OnModelCreating` lines 128–167 and 178–239.
- Produces: five matching configuration classes, including JSON conversions and cascade relationships.

- [ ] **Step 1: Add the five entity attributes**

Add `using Microsoft.EntityFrameworkCore;` and the exact attribute pairing:

| Entity | Attribute |
|---|---|
| `Agent` | `[EntityTypeConfiguration(typeof(AgentConfiguration))]` |
| `AgentAppRelation` | `[EntityTypeConfiguration(typeof(AgentAppRelationConfiguration))]` |
| `AgentSkillRelation` | `[EntityTypeConfiguration(typeof(AgentSkillRelationConfiguration))]` |
| `McpServer` | `[EntityTypeConfiguration(typeof(McpServerConfiguration))]` |
| `AgentMcpServerRelation` | `[EntityTypeConfiguration(typeof(AgentMcpServerRelationConfiguration))]` |

- [ ] **Step 2: Create the five declared configuration classes and use these exact `Configure` bodies**

```csharp
// AgentConfiguration
builder.HasKey(e => e.Id);
builder.HasIndex(e => e.Name).IsUnique();
builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
builder.Property(e => e.Description).HasMaxLength(200);
builder.Property(e => e.SystemPrompt).HasMaxLength(4000);
builder.Property(e => e.Tools).HasMaxLength(4000);
builder.Property(e => e.EnvironmentVariables).HasConversion(
    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
    v => string.IsNullOrWhiteSpace(v)
        ? new Dictionary<string, string>()
        : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v,
              (System.Text.Json.JsonSerializerOptions?)null)
          ?? new Dictionary<string, string>());

// AgentAppRelationConfiguration
builder.HasKey(e => new { e.AgentId, e.AppInstanceId });
builder.HasOne(e => e.Agent)
    .WithMany(agent => agent.AgentAppRelations)
    .HasForeignKey(e => e.AgentId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasOne(e => e.AppInstance)
    .WithMany()
    .HasForeignKey(e => e.AppInstanceId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasIndex(e => e.AppInstanceId);

// AgentSkillRelationConfiguration
builder.HasKey(e => new { e.AgentId, e.SkillId });
builder.HasOne(e => e.Agent)
    .WithMany(a => a.AgentSkillRelations)
    .HasForeignKey(e => e.AgentId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasOne<Skill>()
    .WithMany()
    .HasForeignKey(e => e.SkillId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasIndex(e => e.SkillId);

// McpServerConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
builder.Property(e => e.Description).HasMaxLength(1000);
builder.Property(e => e.TransportType).IsRequired().HasMaxLength(20);
builder.Property(e => e.Command).HasMaxLength(200);
builder.Property(e => e.WorkingDirectory).HasMaxLength(500);
builder.Property(e => e.Url).HasMaxLength(1000);
builder.Property(e => e.Arguments).HasConversion(
    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
    v => string.IsNullOrWhiteSpace(v)
        ? new List<string>()
        : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v,
              (System.Text.Json.JsonSerializerOptions?)null)
          ?? new List<string>());
builder.Property(e => e.EnvironmentVariables).HasConversion(
    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
    v => string.IsNullOrWhiteSpace(v)
        ? new Dictionary<string, string>()
        : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v,
              (System.Text.Json.JsonSerializerOptions?)null)
          ?? new Dictionary<string, string>());
builder.Property(e => e.Headers).HasConversion(
    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
    v => string.IsNullOrWhiteSpace(v)
        ? new Dictionary<string, string>()
        : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v,
              (System.Text.Json.JsonSerializerOptions?)null)
          ?? new Dictionary<string, string>());

// AgentMcpServerRelationConfiguration
builder.HasKey(e => new { e.AgentId, e.McpToolServerId });
builder.HasOne(e => e.Agent)
    .WithMany(a => a.AgentMcpToolServers)
    .HasForeignKey(e => e.AgentId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasOne(e => e.McpToolServer)
    .WithMany(s => s.AgentMcpToolServers)
    .HasForeignKey(e => e.McpToolServerId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasIndex(e => e.McpToolServerId);
```

`AgentSkillRelationConfiguration.cs` also imports `Agw.Shared.Data.Entities.Skills`.

- [ ] **Step 3: Run the agent/tool structural test and verify GREEN**

```bash
dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~AgentAndToolEntities_DeclareMatchingConfigurations"
```

Expected: PASS.

---

### Task 4: Extract agentflow and observability configurations

**Files:**

- Create: `src/server/Agw.Data/Entities/Agentflows/AgentflowConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Agentflows/AgentflowNodeConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Agentflows/AgentflowEdgeConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Observable/AgentflowTraceConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Observable/AgentUsageConfiguration.cs`
- Modify: the five matching entity files to add the EF Core import and attribute.

**Interfaces:**

- Consumes: mappings currently in `AgwDbContext.OnModelCreating` lines 241–292 and 365–374.
- Produces: agentflow graph, trace, and usage metadata configurations.

- [ ] **Step 1: Add the five entity attributes**

| Entity | Attribute |
|---|---|
| `Agentflow` | `[EntityTypeConfiguration(typeof(AgentflowConfiguration))]` |
| `AgentflowNode` | `[EntityTypeConfiguration(typeof(AgentflowNodeConfiguration))]` |
| `AgentflowEdge` | `[EntityTypeConfiguration(typeof(AgentflowEdgeConfiguration))]` |
| `AgentflowTrace` | `[EntityTypeConfiguration(typeof(AgentflowTraceConfiguration))]` |
| `AgentUsage` | `[EntityTypeConfiguration(typeof(AgentUsageConfiguration))]` |

Add `using Microsoft.EntityFrameworkCore;` to each entity. Keep observable configuration files in their current folder but use their entities' existing namespaces: `Agw.Shared.Data.Entities.Agentflows` for `AgentflowTrace` and `Agw.Shared.Data.Entities.Projects` for `AgentUsage`.

- [ ] **Step 2: Create the five classes using these exact bodies**

```csharp
// AgentflowConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
builder.Property(e => e.Description).HasMaxLength(1000);
builder.Property(e => e.SystemPrompt).HasMaxLength(4000);

// AgentflowNodeConfiguration
builder.HasKey(e => new { e.AgentflowId, e.NodeId });
builder.Property(e => e.Kind).HasConversion<int>();
builder.Property(e => e.Name).HasMaxLength(200);
builder.Property(e => e.PositionJson).HasMaxLength(1000);
builder.Property(e => e.Instructions).HasMaxLength(8000);
builder.Property(e => e.ConfigJson).HasMaxLength(16000);
builder.HasIndex(e => new { e.AgentflowId, e.Kind, e.RelateId });

// AgentflowEdgeConfiguration
builder.HasKey(e => new { e.AgentflowId, e.EdgeId });
builder.Property(e => e.Kind).HasConversion<int>();
builder.Property(e => e.Label).HasMaxLength(200);
builder.Property(e => e.ConditionJson).HasMaxLength(8000);
builder.Property(e => e.ConfigJson).HasMaxLength(16000);
builder.HasOne(e => e.SourceNode)
    .WithMany(n => n.SourceEdges)
    .HasForeignKey(e => new { e.AgentflowId, e.SourceNodeId })
    .OnDelete(DeleteBehavior.Cascade);
builder.HasOne(e => e.TargetNode)
    .WithMany(n => n.TargetEdges)
    .HasForeignKey(e => new { e.AgentflowId, e.TargetNodeId })
    .OnDelete(DeleteBehavior.Cascade);

// AgentflowTraceConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
builder.Property(e => e.NodeId).IsRequired().HasMaxLength(200);
builder.Property(e => e.NodeName).HasMaxLength(200);
builder.Property(e => e.NodeKind).HasConversion<string>().HasMaxLength(32);
builder.Property(e => e.AgentName).HasMaxLength(200);
builder.Property(e => e.Input).IsRequired().HasColumnType("text");
builder.Property(e => e.Status).HasConversion<int>();
builder.Property(e => e.Error).HasColumnType("text");
builder.HasIndex(e => new { e.ProjectId, e.ContextId, e.TaskId, e.StartTimeUtc });
builder.HasIndex(e => new { e.AgentflowId, e.NodeId, e.StartTimeUtc });

// AgentUsageConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
builder.Property(e => e.AgentName).IsRequired().HasMaxLength(200);
builder.HasIndex(e => new { e.ProjectId, e.ContextId });
builder.HasIndex(e => e.AgentName);
builder.HasIndex(e => e.RecordedAt);
```

- [ ] **Step 3: Run the agentflow/observable structural test and verify GREEN**

```bash
dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~AgentflowAndObservableEntities_DeclareMatchingConfigurations"
```

Expected: PASS.

---

### Task 5: Extract project and task configurations

**Files:**

- Create: `src/server/Agw.Data/Entities/Projects/ProjectSkillRelationConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Projects/ProjectMcpServerRelationConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Projects/ProjectAppRelationConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Projects/ProjectContextConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Projects/TaskSessionBindingConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Projects/TaskRecordConfiguration.cs`
- Modify: the six matching entity files to add the EF Core import and attribute.
- Preserve: `src/server/Agw.Data/Entities/Projects/Project.cs` and `ProjectConfiguration.cs` as the existing reference implementation.

**Interfaces:**

- Consumes: mappings currently in `AgwDbContext.OnModelCreating` lines 296–363 and 376–418.
- Produces: six project/task configuration classes, including cascade relations and JSON metadata conversion.

- [ ] **Step 1: Add the six entity attributes**

| Entity | Attribute |
|---|---|
| `ProjectSkillRelation` | `[EntityTypeConfiguration(typeof(ProjectSkillRelationConfiguration))]` |
| `ProjectMcpServerRelation` | `[EntityTypeConfiguration(typeof(ProjectMcpServerRelationConfiguration))]` |
| `ProjectAppRelation` | `[EntityTypeConfiguration(typeof(ProjectAppRelationConfiguration))]` |
| `ProjectContext` | `[EntityTypeConfiguration(typeof(ProjectContextConfiguration))]` |
| `TaskSessionBinding` | `[EntityTypeConfiguration(typeof(TaskSessionBindingConfiguration))]` |
| `TaskRecord` | `[EntityTypeConfiguration(typeof(TaskRecordConfiguration))]` |

Add `using Microsoft.EntityFrameworkCore;` to each entity. In `TaskRecord.cs`, do not move or reformat the user's current `[JsonIgnore] public virtual ProjectContext? ProjectContext` declaration.

- [ ] **Step 2: Create the six classes using these exact bodies**

```csharp
// ProjectSkillRelationConfiguration
builder.HasKey(e => new { e.ProjectId, e.SkillId });
builder.HasOne(e => e.Project)
    .WithMany(project => project.ProjectSkillRelations)
    .HasForeignKey(e => e.ProjectId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasOne(e => e.Skill)
    .WithMany()
    .HasForeignKey(e => e.SkillId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasIndex(e => e.SkillId);

// ProjectMcpServerRelationConfiguration
builder.HasKey(e => new { e.ProjectId, e.McpToolServerId });
builder.HasOne(e => e.Project)
    .WithMany(project => project.ProjectMcpToolServers)
    .HasForeignKey(e => e.ProjectId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasOne(e => e.McpToolServer)
    .WithMany()
    .HasForeignKey(e => e.McpToolServerId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasIndex(e => e.McpToolServerId);

// ProjectAppRelationConfiguration
builder.HasKey(e => new { e.ProjectId, e.AppInstanceId });
builder.HasOne(e => e.Project)
    .WithMany(project => project.ProjectAppRelations)
    .HasForeignKey(e => e.ProjectId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasOne(e => e.AppInstance)
    .WithMany()
    .HasForeignKey(e => e.AppInstanceId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasIndex(e => e.AppInstanceId);

// ProjectContextConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.JobId);
builder.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
builder.Property(e => e.Title).IsRequired().HasMaxLength(200).HasDefaultValue("Untitled");
builder.HasIndex(e => new { e.ProjectId, e.ContextId }).IsUnique();
builder.HasIndex(e => e.ProjectId);
builder.HasIndex(e => e.JobId);
builder.HasIndex(e => e.UpdateTime);
builder.HasOne(e => e.Project)
    .WithMany(project => project.Contexts)
    .HasForeignKey(e => e.ProjectId)
    .OnDelete(DeleteBehavior.Cascade);

// TaskSessionBindingConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.ExternalAgentName).IsRequired().HasMaxLength(200);
builder.Property(e => e.ProviderSessionId).IsRequired().HasMaxLength(200);
builder.HasIndex(e => new { e.ProjectContextId, e.AgentId, e.ExternalAgentName }).IsUnique();
builder.HasIndex(e => new { e.ExternalAgentName, e.ProviderSessionId });
builder.HasOne(e => e.ProjectContext)
    .WithMany()
    .HasForeignKey(e => e.ProjectContextId)
    .OnDelete(DeleteBehavior.Cascade);

// TaskRecordConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.TaskId).IsRequired();
builder.Property(e => e.JobId);
builder.Property(e => e.Status).HasConversion<int>();
builder.Property(e => e.TaskErrorMessage).HasMaxLength(2000);
builder.Property(e => e.AgentName).HasMaxLength(200);
builder.Property(e => e.ConversationPayload).HasColumnType("text");
builder.Property(e => e.Error).HasColumnType("text");
builder.HasIndex(e => e.ProjectContextId);
builder.HasIndex(e => new { e.ProjectContextId, e.ConversationSequence });
builder.HasIndex(e => new { e.TaskId, e.CreateTime });
builder.HasIndex(e => new { e.TaskId, e.ConversationSequence }).IsUnique(false);
builder.HasOne(e => e.ProjectContext)
    .WithMany(context => context.Records)
    .HasForeignKey(e => e.ProjectContextId)
    .OnDelete(DeleteBehavior.Cascade);
builder.Property(e => e.Metadata)
    .HasColumnType("jsonb")
    .HasConversion(
        v => v == null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(
                v,
                (System.Text.Json.JsonSerializerOptions?)null),
        v => string.IsNullOrWhiteSpace(v)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                v,
                (System.Text.Json.JsonSerializerOptions?)null));
```

Project relation configurations retain their entity files' existing cross-namespace imports; no new infrastructure dependency is introduced.

- [ ] **Step 3: Run the project/task structural test and verify GREEN**

```bash
dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~ProjectAndTaskEntities_DeclareMatchingConfigurations"
```

Expected: PASS.

---

### Task 6: Extract job and integration configurations

**Files:**

- Create: `src/server/Agw.Data/Entities/Jobs/JobConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Jobs/JobLogConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Integrations/AppInstanceConfiguration.cs`
- Create: `src/server/Agw.Data/Entities/Integrations/OAuthAuthorizationTokenConfiguration.cs`
- Modify: the four matching entity files to add the EF Core import and attribute.

**Interfaces:**

- Consumes: mappings currently in `AgwDbContext.OnModelCreating` lines 420–474.
- Produces: four configurations covering scheduling indexes, optimistic concurrency, and OAuth token ownership.

- [ ] **Step 1: Add the four entity attributes**

| Entity | Attribute |
|---|---|
| `Job` | `[EntityTypeConfiguration(typeof(JobConfiguration))]` |
| `JobLog` | `[EntityTypeConfiguration(typeof(JobLogConfiguration))]` |
| `AppInstance` | `[EntityTypeConfiguration(typeof(AppInstanceConfiguration))]` |
| `OAuthAuthorizationToken` | `[EntityTypeConfiguration(typeof(OAuthAuthorizationTokenConfiguration))]` |

Add `using Microsoft.EntityFrameworkCore;` to each entity.

- [ ] **Step 2: Create the four classes using these exact bodies**

```csharp
// JobConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
builder.Property(e => e.Prompt).HasMaxLength(4000);
builder.Property(e => e.TriggerType).HasConversion<int>();
builder.Property(e => e.TriggerValue).IsRequired().HasMaxLength(200);
builder.Property(e => e.Status).HasConversion<int>();
builder.Property(e => e.LastError).HasMaxLength(2000);
builder.Property(e => e.RowVersion)
    .IsRequired()
    .IsConcurrencyToken()
    .ValueGeneratedNever();
builder.HasIndex(e => new { e.IsEnabled, e.Status, e.NextRunTime })
    .HasDatabaseName("ix_task_next_run_time");
builder.HasIndex(e => e.ProjectId)
    .HasDatabaseName("ix_task_project");

// JobLogConfiguration
builder.HasKey(e => e.Id);
builder.Property(e => e.JobId);
builder.Property(e => e.TaskId);
builder.Property(e => e.ErrorMessage).HasMaxLength(2000);
builder.HasIndex(e => new { e.JobId, e.StartTime });

// AppInstanceConfiguration
builder.HasKey(e => e.Id);
builder.HasIndex(e => e.AppName).IsUnique(false);
builder.HasIndex(e => e.ClientId).IsUnique();
builder.Property(e => e.AppName).IsRequired().HasMaxLength(128);
builder.Property(e => e.ClientId).IsRequired().HasMaxLength(200);
builder.Property(e => e.ClientSecret).IsRequired().HasMaxLength(2000);
builder.HasOne(e => e.AuthorizationToken)
    .WithOne()
    .HasForeignKey<OAuthAuthorizationToken>(e => e.AppInstanceId)
    .OnDelete(DeleteBehavior.Cascade);

// OAuthAuthorizationTokenConfiguration
builder.HasKey(e => e.Id);
builder.HasIndex(e => e.AppInstanceId).IsUnique();
builder.HasIndex(e => e.ExpiresAtUtc);
builder.Property(e => e.AppInstanceId).IsRequired();
builder.Property(e => e.Subject).IsRequired().HasMaxLength(200);
builder.Property(e => e.AccessToken).IsRequired().HasMaxLength(4000);
builder.Property(e => e.RefreshToken).HasMaxLength(4000);
builder.Property(e => e.TokenType).IsRequired().HasMaxLength(50);
```

- [ ] **Step 3: Run the job/integration test and whole structural suite**

```bash
dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~JobAndIntegrationEntities_DeclareMatchingConfigurations|FullyQualifiedName~PersistedEntities_AllDeclareMatchingConfigurations"
```

Expected: PASS; the assembly guard sees 26 `[Table]` types and a matching configuration for each.

---

### Task 7: Remove centralized mappings and verify model equivalence

**Files:**

- Modify: `src/server/Agw.Infrastructure/Data/AgwDbContext.cs`
- Verify: existing EF model and persistence tests across `tests/`.

**Interfaces:**

- Consumes: all 26 attribute-discovered configuration classes.
- Produces: an `AgwDbContext` with no entity-level `OnModelCreating` mappings.

- [ ] **Step 1: Run the existing model tests before context cleanup**

```bash
dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~PersistenceModelTests|FullyQualifiedName~TaskProjectionModelTests|FullyQualifiedName~JobRowVersionTests|FullyQualifiedName~AgwDbContextIntegrationTests"
dotnet test tests/Agw.Host.Tests/Agw.Host.Tests.csproj --filter "FullyQualifiedName~ModelTests"
```

Expected: PASS with both the attribute configurations and the still-present identical context mappings.

- [ ] **Step 2: Remove only the centralized entity mapping code**

Delete the complete `OnModelCreating(ModelBuilder modelBuilder)` override from `AgwDbContext.cs` and delete its now-unused `using System.Text.Json;`. Keep `OnConfiguring`, all `DbSet` properties, save overrides, `StampJobRowVersions`, and `PruneDeletedRelations` unchanged.

- [ ] **Step 3: Run focused structural and model tests after cleanup**

```bash
dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~EntityTypeConfigurationTests|FullyQualifiedName~PersistenceModelTests|FullyQualifiedName~TaskProjectionModelTests|FullyQualifiedName~JobRowVersionTests|FullyQualifiedName~AgwDbContextIntegrationTests"
dotnet test tests/Agw.Host.Tests/Agw.Host.Tests.csproj --filter "FullyQualifiedName~ModelTests"
```

Expected: PASS, proving configuration discovery works without the centralized mappings.

- [ ] **Step 4: Check for pending model changes without creating a migration**

```bash
dotnet ef migrations has-pending-model-changes -p src/server/Agw.Infrastructure -s src/server/Agw.Host
```

Expected: exit 0 and no pending model changes. If the repository already has unrelated pending model changes, record them and do not generate a migration.

- [ ] **Step 5: Run repository-wide backend verification**

```bash
dotnet test Agw.slnx
dotnet build Agw.slnx --no-restore
git diff --check
```

Expected: all tests pass, the build exits 0, and `git diff --check` reports no whitespace errors.

- [ ] **Step 6: Audit the final diff**

```bash
git status --short
git diff --stat
git diff -- src/server/Agw.Data src/server/Agw.Infrastructure/Data/AgwDbContext.cs tests/Agw.Projects.Tests/Infrastructure/EntityTypeConfigurationTests.cs
```

Confirm all changes trace to the requested extraction, `AppDefinition` is untouched, no migration exists, user-owned edits remain, and nothing was committed or staged by this implementation.
