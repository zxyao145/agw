# Empty Message Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop empty chat messages from creating `TaskRecord` rows while preserving messages with non-text protocol content.

**Architecture:** Filter once at `EfCoreChatHistoryProvider.AppendAsync`, before database work, because it is the common persistence boundary. Treat a message as persistable when at least one content item is non-text or contains non-whitespace text.

**Tech Stack:** .NET 10, EF Core, Microsoft.Extensions.AI, xUnit v3, SQLite in-memory tests.

## Global Constraints

- Follow `AGENTS.md`, `docs/rules.md`, and `docs/superpowers/specs/2026-07-14-empty-message-persistence-design.md`.
- Preserve function calls, function results, approvals, attachments, and other non-text content.
- Do not create or update a project context when every supplied message is empty.
- Do not modify database schemas, migrations, API contracts, frontend files, or unrelated dirty files.
- Do not stage or commit; the user has not authorized Git writes.

---

### Task 1: Filter Empty Messages at the Shared Write Boundary

**Files:**
- Modify: `tests/Agw.Projects.Tests/EfCoreChatHistoryProviderTests.cs`
- Modify: `src/server/Agw.Projects/Domain/Services/EfCoreChatHistoryProvider.cs`

**Interfaces:**
- Consumes: `EfCoreChatHistoryProvider.AppendAsync(Guid, string, IReadOnlyList<ChatMessage>, CancellationToken)`.
- Produces: an internal `HasContent(ChatMessage)` predicate used only by the shared append implementation.

- [ ] **Step 1: Add the failing regression test**

Add a test that supplies empty-string and whitespace-only assistant messages together with a function-call assistant message:

```csharp
[Fact]
public async Task AppendAsync_WhenMessagesContainBlankText_PersistsOnlyMessagesWithContent()
{
    // Arrange an in-memory SQLite database and seeded project using the
    // existing setup pattern in this test class.
    var functionCall = new ChatMessage(
        ChatRole.Assistant,
        [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())]);

    await writer.AppendAsync(
        projectId,
        "context-1",
        [
            new ChatMessage(ChatRole.Assistant, string.Empty),
            new ChatMessage(ChatRole.Assistant, "   "),
            functionCall
        ],
        cancellationToken);

    await using var verifyContext = new AgwDbContext(options);
    var record = await verifyContext.TaskRecords.SingleAsync(cancellationToken);
    var persisted = JsonSerializer.Deserialize<ChatMessage>(record.ConversationPayload!);
    var persistedCall = Assert.IsType<FunctionCallContent>(Assert.Single(persisted!.Contents));
    Assert.Equal("call-1", persistedCall.CallId);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test tests/Agw.Projects.Tests --filter "FullyQualifiedName~AppendAsync_WhenMessagesContainBlankText_PersistsOnlyMessagesWithContent"
```

Expected: FAIL because three `TaskRecord` rows are persisted and `SingleAsync` finds more than one row.

- [ ] **Step 3: Implement the minimal shared-boundary filter**

At the start of the private `AppendAsync` overload, replace the count-only guard with a filtered list:

```csharp
ArgumentNullException.ThrowIfNull(messages);
var persistableMessages = messages.Where(HasContent).ToList();
if (persistableMessages.Count == 0)
{
    return;
}
```

Use `persistableMessages` instead of `messages` for the first-user-title lookup and record-creation loop. Add this predicate near the other private message helpers:

```csharp
private static bool HasContent(ChatMessage message) =>
    message.Contents.Any(content =>
        content is not TextContent textContent ||
        !string.IsNullOrWhiteSpace(textContent.Text));
```

- [ ] **Step 4: Run the focused test and verify GREEN**

Run:

```bash
dotnet test tests/Agw.Projects.Tests --filter "FullyQualifiedName~AppendAsync_WhenMessagesContainBlankText_PersistsOnlyMessagesWithContent"
```

Expected: PASS.

- [ ] **Step 5: Run module and repository verification**

Run:

```bash
dotnet test tests/Agw.Projects.Tests
dotnet test Agw.slnx
```

Expected: both commands pass with no new warnings or errors caused by these changes.

- [ ] **Step 6: Review the final diff**

Run:

```bash
git diff --check
git diff -- src/server/Agw.Projects/Domain/Services/EfCoreChatHistoryProvider.cs tests/Agw.Projects.Tests/EfCoreChatHistoryProviderTests.cs docs/superpowers/specs/2026-07-14-empty-message-persistence-design.md docs/superpowers/plans/2026-07-14-empty-message-persistence.md
```

Expected: only the approved filter, its regression test, and these planning documents appear; whitespace checks pass.
