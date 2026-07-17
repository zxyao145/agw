# Desktop Platform Packages Relocation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the Desktop-only workspace packages below `src/clients/desktop/packages/` without changing package names, public imports, renderer behavior, or Electron runtime behavior.

**Architecture:** Root `src/clients/packages/` contains cross-client infrastructure and business domains. Desktop platform adapters live below the owning Electron application as nested workspace packages: `desktop/packages/contracts` and `desktop/packages/renderer`. The Next.js application continues to compile `@agw/desktop-renderer`; Electron main/preload continue to consume `@agw/desktop-contracts`.

**Tech Stack:** pnpm 11.7.0 Workspace, Turborepo 2.10.5, TypeScript 5.9.3, Next.js 16.2.1, Electron Forge 7.11.2.

## Global Constraints

- Keep package names `@agw/desktop-contracts` and `@agw/desktop-renderer` unchanged.
- Keep the single Next.js renderer; do not create a second Desktop React application.
- Keep Mobile outside the pnpm Workspace.
- Preserve existing staged and unrelated worktree changes.
- Do not create a Git commit.
- Exclude `desktop/packages/` source from Electron Forge output; only prepared renderer resources and compiled Electron entry points belong in the application package.

---

### Task 1: Make the desired package ownership executable

**Files:**
- Modify: `src/clients/tools/scripts/check-client-boundaries.mjs`

**Interfaces:**
- Consumes: current workspace filesystem and package manifests.
- Produces: a failing boundary check until both Desktop packages live under `desktop/packages/` and no old top-level copies remain.

- [x] Add assertions for `desktop/packages/contracts/package.json` and `desktop/packages/renderer/package.json`.
- [x] Assert `packages/desktop-contracts` and `packages/desktop-renderer` do not exist.
- [x] Include both cross-client and Desktop package roots in source/import/self-import validation.
- [x] Run `pnpm test:boundaries`; expect failure reporting the missing nested Desktop package.

### Task 2: Move both workspace packages and update workspace/build configuration

**Files:**
- Move: `src/clients/packages/desktop-contracts/` → `src/clients/desktop/packages/contracts/`
- Move: `src/clients/packages/desktop-renderer/` → `src/clients/desktop/packages/renderer/`
- Modify: `src/clients/desktop/packages/renderer/tsconfig.json`
- Modify: `src/clients/pnpm-workspace.yaml`
- Modify: `src/clients/desktop/forge.config.cjs`
- Modify: `src/clients/pnpm-lock.yaml`

**Interfaces:**
- Consumes: existing package names and public exports.
- Produces: the same `@agw/desktop-contracts` and `@agw/desktop-renderer` imports at new physical paths.

- [x] Move only package source/configuration files; do not move `.turbo`, `dist`, `node_modules`, or `.DS_Store` artifacts.
- [x] Change the renderer package TypeScript base path to `../../../tsconfig.react.json`.
- [x] Add `desktop/packages/*` to the Workspace while retaining `packages/*`, `desktop`, and `web`.
- [x] Add `/packages` to the Electron Forge ignore expressions.
- [x] Run `pnpm install --lockfile-only` so lock importers become `desktop/packages/contracts` and `desktop/packages/renderer`.
- [x] Run `pnpm test:boundaries`; expect the relocation assertions to pass.

### Task 3: Synchronize active architecture documentation

**Files:**
- Modify: `AGENTS.md`
- Modify: `CLAUDE.md`
- Modify: `README.md`
- Modify: `docs/1.Development.md`
- Modify: `docs/2.Architecture.md`
- Modify: `docs/superpowers/specs/2026-07-17-client-domain-packages-design.md`
- Modify: `src/clients/directory-structure.md`
- Modify: `src/clients/desktop/README.md`
- Modify: `src/clients/desktop/packages/contracts/README.md`

**Interfaces:**
- Consumes: the relocated directory layout.
- Produces: current paths and ownership rules for developers and agents.

- [x] Replace active `src/clients/packages/desktop-*` paths with `src/clients/desktop/packages/*` paths.
- [x] Document that root packages are cross-client and nested Desktop packages are platform adapters.
- [x] Keep `AGENTS.md` and `CLAUDE.md` identical.

### Task 4: Verify Workspace, renderer, and package behavior

**Files:**
- Verify only; no planned source changes.

**Interfaces:**
- Consumes: completed relocation.
- Produces: evidence that dependency resolution and packaged output are unchanged.

- [x] Run `pnpm install --frozen-lockfile` from `src/clients`.
- [x] Run `pnpm test`, `pnpm lint`, `pnpm format:check`, and `pnpm build`.
- [x] Run `pnpm prepare:renderer` and verify `desktop/resources/renderer/index.html`.
- [x] Run the client-flavor Desktop package under Node 24 and verify the `.app` plus `app.asar` output.
- [x] Inspect `app.asar` or Forge inputs to confirm nested `desktop/packages/` source is excluded.
- [x] Run `cmp AGENTS.md CLAUDE.md` and `git diff --check`.
