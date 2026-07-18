# Client App Isolation Implementation Plan

> Superseded on 2026-07-18 by `2026-07-18-desktop-react-renderer.md` after Web and Desktop were required to own independent renderers and build outputs.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Web and Desktop independent applications that consume only root `src/clients/packages/*` workspace packages, while keeping one Next.js renderer.

**Architecture:** Keep the Electron bridge protocol as the root `@agw/desktop-contracts` package because both applications consume that interface. Delete `@agw/desktop-renderer`; its React implementation belongs to Web under `web/src/adapters/electron`. Remove Electron knowledge from the Auth and Settings domain packages and compose Electron-specific behavior in Web.

**Tech Stack:** pnpm 11, Turborepo 2, TypeScript 5.9, Next.js 16, React 19, Electron 43.

## Global Constraints

- `src/clients/web` and `src/clients/desktop` must not import each other or depend on each other's workspace package.
- Application code may depend only on workspace packages rooted at `src/clients/packages/*`.
- Keep a single Next.js renderer; do not create a second React application.
- Preserve application behavior and existing package versions.
- Do not create a Git commit or alter the existing staged snapshot.

---

### Task 1: Encode the application-isolation rule

**Files:**
- Modify: `src/clients/tools/scripts/check-client-boundaries.mjs`

**Interfaces:**
- Consumes: workspace manifests and TypeScript source files.
- Produces: a failing boundary check until contracts are rooted under `packages/`, renderer package references are removed, and app-to-app imports are absent.

- [x] Update the boundary check to require `packages/desktop-contracts`, forbid `desktop/packages`, forbid `@agw/desktop-renderer`, and reject Web/Desktop cross-imports.
- [x] Run `pnpm test:boundaries` and verify it fails on the current nested Desktop packages.

### Task 2: Restore the shared bridge protocol to root packages

**Files:**
- Move: `src/clients/desktop/packages/contracts/**` to `src/clients/packages/desktop-contracts/**`
- Modify: `src/clients/pnpm-workspace.yaml`
- Modify: `src/clients/.gitignore`
- Modify: `src/clients/turbo.json`
- Modify: `src/clients/desktop/forge.config.cjs`

**Interfaces:**
- Produces: `@agw/desktop-contracts` from `packages/desktop-contracts`.

- [x] Move the contract sources without generated output.
- [x] Remove the nested Desktop workspace pattern and nested-package ignore exceptions.
- [x] Keep the existing contract build task and exclude root workspace sources from Electron packaging.

### Task 3: Make the Electron React adapter Web-owned

**Files:**
- Move: `src/clients/desktop/packages/renderer/src/**` to `src/clients/web/src/adapters/electron/**`
- Delete: `src/clients/desktop/packages/renderer/package.json`
- Delete: `src/clients/desktop/packages/renderer/tsconfig.json`
- Modify: `src/clients/web/package.json`
- Modify: `src/clients/web/next.config.ts`
- Modify: `src/clients/web/src/app/layout.tsx`
- Modify: `src/clients/web/src/app/(app)/layout.tsx`

**Interfaces:**
- Consumes: `@agw/desktop-contracts`, `@agw/api`, `@agw/chat`, `@agw/components`, and `@agw/projects`.
- Produces: Web-local Electron runtime, shell, and project-picker modules.

- [x] Move renderer implementation and tests to `web/src/adapters/electron`.
- [x] Replace `@agw/desktop-renderer` imports with Web-local imports.
- [x] Remove the renderer workspace dependency and Next transpilation entry.

### Task 4: Remove Electron knowledge from domain packages

**Files:**
- Modify: `src/clients/packages/auth/src/ui-web/components/auth-gate.tsx`
- Modify: `src/clients/packages/auth/package.json`
- Modify: `src/clients/packages/settings/src/ui-web/pages/settings/page.tsx`
- Modify: `src/clients/packages/settings/package.json`
- Create: `src/clients/web/src/adapters/electron/desktop-connection-gate.tsx`
- Create: `src/clients/web/src/adapters/electron/desktop-settings-page.tsx`
- Modify: `src/clients/web/src/app/(app)/layout.tsx`
- Modify: `src/clients/web/src/app/(app)/settings/page.tsx`

**Interfaces:**
- `AuthGate` handles browser session authentication only.
- `DesktopConnectionGate` handles Electron Server connection readiness.
- `SettingsPage` handles browser security settings only.
- `DesktopSettingsPage` handles Electron profiles, close behavior, package information, and uninstall preparation.

- [x] Extract the Desktop connection gate from Auth without changing its states or messages.
- [x] Extract Desktop settings into the Web Electron adapter.
- [x] Compose browser/Desktop flows in the Web application routes.
- [x] Remove `@agw/desktop-renderer` from Auth and Settings manifests.
- [x] Run focused tests and `pnpm test:boundaries` until green.

### Task 5: Synchronize the workspace and documentation

**Files:**
- Modify: `src/clients/pnpm-lock.yaml`
- Modify: `src/clients/directory-structure.md`
- Modify: `src/clients/desktop/README.md`
- Modify: `AGENTS.md`
- Modify: `CLAUDE.md`
- Modify: active client architecture/design documents that describe the package layout.

**Interfaces:**
- Produces: one lockfile and documentation matching the app-isolation rule.

- [x] Run `pnpm install` to update importers without upgrading application dependencies.
- [x] Document the root Desktop contract package and Web-owned Electron adapter.
- [x] Keep `AGENTS.md` and `CLAUDE.md` identical.

### Task 6: Verify the first isolation pass

**Files:**
- Verify only.

**Interfaces:**
- Produces: evidence that the workspace dependency graph, tests, builds, renderer export, and Desktop package remain valid.

- [x] Run `pnpm install --frozen-lockfile`.
- [x] Run `pnpm test`, `pnpm lint`, `pnpm format:check`, and `pnpm build`.
- [x] Run `pnpm prepare:renderer` and package the Desktop client flavor.
- [x] Confirm no `@agw/desktop-renderer`, `desktop/packages`, or Web/Desktop cross-import remains.
- [x] Run `cmp AGENTS.md CLAUDE.md` and `git diff --check`.
