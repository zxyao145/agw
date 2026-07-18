# Client Domain Packages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move Web infrastructure, reusable UI, and all current business domains into pnpm workspace packages while preserving Next.js and Electron renderer behavior.

**Architecture:** `@agw/web` remains the Next.js route and platform composition package. Source workspace packages own browser UI and domain behavior, `@agw/api` wraps `@agw/http-client`, and no package imports Web application internals.

**Tech Stack:** pnpm 11.7.0, Turborepo 2.10.5, TypeScript 5.9.3, Next.js 16.2.1, React 19.2.4, Electron 43.1.1, oxlint, oxfmt, tsx.

## Global Constraints

- Work only in `/Users/ben/source/repos/agw/.worktrees/agw-desktop-v1` on `codex/agw-desktop-v1`.
- Preserve the user's staged `src/clients/directory-structure.md` change and do not stage or commit.
- Keep `@agw/web` as the Electron renderer; do not add `desktop/src/renderer`.
- Keep Mobile outside the pnpm workspace.
- Preserve routes, payloads, persistence, API, SignalR, and Electron IPC behavior.
- Do not create empty package folders or speculative interfaces.
- No package may import `@/`, `@agw/web`, or a path below `web/src`.

---

### Task 1: Establish package build conventions

**Files:**
- Create: `src/clients/tsconfig.json`
- Create: `src/clients/tsconfig.react.json`
- Modify: `src/clients/turbo.json`

- [ ] Add strict shared TypeScript compiler settings and a React extension for source packages.
- [ ] Add Turbo `typecheck`/build ordering without caching side-effecting tasks.
- [ ] Run the existing workspace tests as the baseline.

### Task 2: Extract transport and generated API packages

**Files:**
- Create: `src/clients/packages/http-client/**`
- Create: `src/clients/packages/api/**`
- Move: generic request tests and `web/src/api/openapi.d.ts`
- Modify: Web and domain imports plus package manifests.

- [ ] Move framework-free URL, query, response, envelope, and error behavior to `@agw/http-client` with the existing tests.
- [ ] Move typed OpenAPI request functions and generated types to `@agw/api`.
- [ ] Keep auth, execution, file, and task orchestration for their owning domain tasks.
- [ ] Run focused package tests and type checks.

### Task 3: Extract business-neutral components

**Files:**
- Create: `src/clients/packages/components/**`
- Move: `web/src/components/ui/**`, generic table/form/provider components, `web/src/lib/utils.ts`, `web/src/hooks/use-mobile.ts`, and design tokens.
- Modify: all component imports and `web/src/app/globals.css`.

- [ ] Move shadcn primitives and generic widgets under `ui-web`.
- [ ] Extract reusable CSS variables to `ui-tokens` and import them from the Web global stylesheet.
- [ ] Ensure components depend on no business package or Web application alias.
- [ ] Run component tests, lint, and type checking.

### Task 4: Extract cross-cutting Auth, Chat, Projects, and Agents domains

**Files:**
- Create: `src/clients/packages/auth/**`
- Create: `src/clients/packages/chat/**`
- Create: `src/clients/packages/projects/**`
- Create: `src/clients/packages/agents/**`
- Modify: matching Next.js route files into thin exports.

- [ ] Move auth service, login page, and AuthGate into `@agw/auth`.
- [ ] Move execution hub/state, message rendering, and chat workspace into `@agw/chat`.
- [ ] Move project/task/file APIs and UI into `@agw/projects`.
- [ ] Move Agents/Agentflows pages, components, and types into `@agw/agents`.
- [ ] Replace internal aliases with public package imports and run focused tests.

### Task 5: Extract remaining route domains

**Files:**
- Create: `src/clients/packages/providers/**`
- Create: `src/clients/packages/integrations/**`
- Create: `src/clients/packages/jobs/**`
- Create: `src/clients/packages/skills/**`
- Create: `src/clients/packages/observability/**`
- Create: `src/clients/packages/settings/**`
- Create: `src/clients/packages/desktop-renderer/**`
- Modify: matching Next.js route files into thin exports.

- [ ] Move each route implementation and its tests into its owning package.
- [ ] Move the existing Web Desktop runtime feature into `@agw/desktop-renderer` so domain packages never import Web internals.
- [ ] Move MCP management with Integrations and Models with Providers.
- [ ] Keep route paths and default exports unchanged through route wrappers.
- [ ] Run each package's focused tests and type checking.

### Task 6: Finalize application composition and workspace metadata

**Files:**
- Modify: `src/clients/web/package.json`, `src/clients/web/next.config.ts`, `src/clients/web/tsconfig.json`
- Modify: `src/clients/pnpm-lock.yaml`, `src/clients/turbo.json`
- Modify: client documentation and repository agent instructions where commands or maps changed.

- [ ] Declare every direct workspace dependency explicitly.
- [ ] Configure Next.js to transpile source workspace packages and Tailwind to scan them.
- [ ] Delete Web directories made empty by the migration.
- [ ] Scan for prohibited package-to-Web imports and stale aliases.

### Task 7: Verify the complete workspace

- [ ] Run `pnpm install --frozen-lockfile` from `src/clients` under Node 24.
- [ ] Run `pnpm format:check`, `pnpm lint`, `pnpm test`, and `pnpm build`.
- [ ] Run a second build and confirm local Turbo cache hits.
- [ ] Run `pnpm prepare:renderer` and verify the Desktop renderer output.
- [ ] Run `git diff --check`, compare `AGENTS.md` and `CLAUDE.md`, and confirm the user's staged file remains staged and unchanged by this implementation.
