# CategoryType JSON Serialization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serialize and deserialize `CategoryType` by enum member name.

**Architecture:** Attach `JsonStringEnumConverter` directly to `CategoryType` so the behavior remains local to this enum. Verify both serialization and deserialization with one focused xUnit test.

**Tech Stack:** .NET 10, System.Text.Json, xUnit

## Global Constraints

- Do not change global JSON options or unrelated enums.
- Do not stage, commit, push, create a PR, or apply a migration.

---

### Task 1: CategoryType string serialization

**Files:**
- Modify: `src/server/Agw.Data/Entities/Integrations/CategoryType.cs`
- Create: `tests/Agw.Integrations.Tests/CategoryTypeJsonTests.cs`

**Interfaces:**
- Consumes: `System.Text.Json.Serialization.JsonStringEnumConverter`
- Produces: `CategoryType` JSON values represented by enum member names

- [ ] **Step 1: Write the failing serialization test**

```csharp
using System.Text.Json;

using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Integrations.Tests;

public class CategoryTypeJsonTests
{
    [Fact]
    public void JsonSerialization_RoundTripsEnumName()
    {
        var json = JsonSerializer.Serialize(CategoryType.GitServer);
        var value = JsonSerializer.Deserialize<CategoryType>(json);

        Assert.Equal("\"GitServer\"", json);
        Assert.Equal(CategoryType.GitServer, value);
    }
}
```

- [ ] **Step 2: Run the test and verify the red state**

Run: `dotnet test tests/Agw.Integrations.Tests/Agw.Integrations.Tests.csproj --filter "FullyQualifiedName~CategoryTypeJsonTests" --no-restore`

Expected: FAIL because the current serialized value is `0` instead of `"GitServer"`.

- [ ] **Step 3: Add the enum-level converter**

```csharp
using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Integrations;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CategoryType
```

- [ ] **Step 4: Run focused and integration tests**

Run: `dotnet test tests/Agw.Integrations.Tests/Agw.Integrations.Tests.csproj --no-restore`

Expected: all tests pass, including `CategoryTypeJsonTests`.

- [ ] **Step 5: Verify the diff**

Run: `git diff --check && git diff --cached --quiet`

Expected: no whitespace errors and no staged changes.
