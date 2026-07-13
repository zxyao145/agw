# Agent Turn Summary Markdown Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow `AgentTurnSummaryService` to request and preserve Markdown summaries when Markdown improves readability.

**Architecture:** Keep the existing result message and persistence pipeline unchanged. Change only the shared summary prompt, regression expectations, and execution documentation so Definition Agent and Agentflow Output summaries inherit the behavior automatically.

**Tech Stack:** .NET 10, C#, Microsoft.Extensions.AI, xUnit

## Global Constraints

- Markdown is optional and should be used only when it improves readability.
- A result remains one System-role `TextContent` authored by `$agw-server` with `type=result`.
- Do not add API fields, database columns, content types, post-processing, or per-agent format configuration.
- Do not create a Git commit without explicit user authorization.

---

### Task 1: Optional Markdown Summary Output

**Files:**
- Modify: `tests/Agw.Agents.Tests/AgentTurnSummaryServiceTests.cs`
- Modify: `src/server/Agw.Agents/Execution/Summaries/AgentTurnSummaryService.cs`
- Modify: `src/server/Agw.Agents/Execution/README.md`

**Interfaces:**
- Consumes: `AgentTurnSummaryService.CreateResultAsync(...)`
- Produces: the existing `ChatMessage` result contract with optionally formatted Markdown text

- [ ] **Step 1: Write the failing test**

Update `CreateResultAsync_Success_ReturnsAndPersistsTextResultWithUsage` so the fake model returns Markdown and assert that the system prompt explicitly permits optional Markdown:

```csharp
new ChatMessage(ChatRole.Assistant, "  ## 完成\n\n- 已支持 **Markdown**。  ")

Assert.Equal("## 完成\n\n- 已支持 **Markdown**。", text.Text);
Assert.Contains("Use Markdown when it improves readability", client.Messages[0].Text);
Assert.Contains("Plain text is also acceptable", client.Messages[0].Text);
Assert.DoesNotContain("as plain text", client.Messages[0].Text);
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore --filter "FullyQualifiedName~CreateResultAsync_Success_ReturnsAndPersistsTextResultWithUsage"
```

Expected: FAIL because the current system prompt requires plain text and does not allow optional Markdown.

- [ ] **Step 3: Implement the minimal prompt change**

Replace the final sentence of `DefaultInstructions` with:

```csharp
"Return only the summary text. Use Markdown when it improves readability; Plain text is also acceptable. " +
"Do not return JSON, XML, wrapper objects, or transport metadata.";
```

- [ ] **Step 4: Document the behavior**

Update the Result Summary section in `Execution/README.md` to state that result text may use Markdown when useful and remains a single `TextContent`.

- [ ] **Step 5: Verify GREEN and regression coverage**

Run:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore
dotnet build Agw.slnx --no-restore
git diff --check
```

Expected: all `Agw.Agents.Tests` pass, the solution builds with zero errors, and `git diff --check` reports no errors.
