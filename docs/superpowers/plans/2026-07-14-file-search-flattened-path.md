# Flattened Path File Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `/api/files/search` match complete normalized relative paths so directory matches naturally include descendant entries.

**Architecture:** Keep traversal in `FilesController`, but enumerate all permitted entries and filter their normalized relative paths rather than passing the keyword to file-name enumeration. Directories use a trailing `/` both for matching and in `relativePath` responses.

**Tech Stack:** ASP.NET Core, C# 14, xUnit, .NET 10

## Global Constraints

- Preserve hidden-directory, ignored-directory, ignored-file, sorting, and limit behavior.
- Do not change the API response shape or generated clients.
- Do not create a Git commit without explicit user authorization.

---

### Task 1: Specify flattened-path matching

**Files:**
- Modify: `tests/Agw.Files.Tests/FilesControllerSearchTests.cs`

**Interfaces:**
- Consumes: `FilesController.SearchAsync(string? path, string? keyword, int limit = 10, bool recursive = true)`
- Produces: Regression coverage for plain and separator-bearing path queries.

- [x] **Step 1: Write failing recursive-search tests**

Create `demo/abc/d.txt` and `demo/abc/e.log`. Assert that both `a` and `abc/` return `demo/abc/` plus both descendant files.

- [x] **Step 2: Verify the tests fail for the missing behavior**

Run:

```bash
dotnet test tests/Agw.Files.Tests --filter 'FullyQualifiedName~FilesControllerSearchTests'
```

Expected: the new tests fail because files are filtered by basename and directories do not use a trailing `/`.

### Task 2: Match normalized flattened paths

**Files:**
- Modify: `src/server/Agw.Files/Controllers/FilesController.cs`

**Interfaces:**
- Consumes: filesystem paths produced by `Directory.EnumerateDirectories` and `Directory.EnumerateFiles`.
- Produces: normalized `FileSearchResult.RelativePath` values and full-path keyword filtering.

- [x] **Step 1: Add normalized relative-path helpers**

Build relative paths with `Path.GetRelativePath`, normalize separators to `/`, append `/` to directories, and compare the complete value with `StringComparison.OrdinalIgnoreCase`.

- [x] **Step 2: Filter recursive entries by complete path**

Enumerate every permitted direct file and directory. Add only entries whose normalized relative path matches, then continue recursion through permitted directories even when the directory itself does not match.

- [x] **Step 3: Filter non-recursive entries by complete path**

Apply the same normalized-path comparison to direct children without descending.

- [x] **Step 4: Verify focused behavior**

Run:

```bash
dotnet test tests/Agw.Files.Tests --filter 'FullyQualifiedName~FilesControllerSearchTests'
```

Expected: all flattened-path controller tests pass.

- [x] **Step 5: Verify build and module baseline**

Run:

```bash
dotnet build Agw.slnx --no-restore
dotnet test tests/Agw.Files.Tests
git diff --check
```

Expected: the solution builds; search tests pass; any unrelated pre-existing module failures are reported separately; the diff has no whitespace errors.
