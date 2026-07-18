# Client Domain Packages Design

## Goal

Move business code out of application directories into independently owned pnpm workspace packages while giving Web and Desktop separate thin Next.js route shells.

## Boundaries

- `@agw/web` owns browser Next.js routes, global CSS, and root layouts.
- `@agw/desktop` owns Electron main/preload, an internal Next.js renderer, and its cross-process contracts.
- `@agw/http-client` owns framework-free request construction, response parsing, Bens.Results envelope handling, and HTTP errors.
- `@agw/api` owns generated OpenAPI types and the typed request facade.
- `@agw/components` owns business-neutral Web UI primitives, reusable widgets, and design tokens.
- `@agw/desktop` owns Electron bridge runtime composition under `desktop/renderer/src/runtime`; domain packages do not depend on that adapter.
- `desktop/src/shared/contracts` defines the framework-free seam shared only by Desktop main, preload, and renderer implementations.
- Business packages own their services, types, state, hooks, components, and reusable page implementations.
- No workspace package may import from `@agw/web` or the `web/src` alias.
- Mobile stays outside the pnpm workspace and no empty native UI directories are created.

## Business Packages

- `@agw/auth`: browser authentication service, login page, and authentication gate.
- `@agw/agents`: Agents and Agentflows management and execution UI.
- `@agw/projects`: Projects, contexts, tasks, file exploration, and project-scoped task helpers.
- `@agw/chat`: chat workspace, message rendering, SignalR execution flow, and execution state.
- `@agw/providers`: Providers and Models management.
- `@agw/integrations`: plugin connections, MCP tool servers, and capability selection UI.
- `@agw/jobs`: scheduled jobs and job logs.
- `@agw/skills`: Skill management.
- `@agw/observability`: dashboard and trace views.
- `@agw/settings`: browser Server/account settings UI; Web composes Electron-specific settings in its Electron adapter.

Agent selection is owned by `@agw/agents`; chat rendering is owned by `@agw/chat`; file exploration and task lists are owned by `@agw/projects`. Cross-domain consumers import only these packages' public exports.

## Source Package Model

React workspace packages export TypeScript source through explicit package subpaths. Next.js compiles them from the `src/clients` Turbopack root. Each package has independent `lint`, `test`, `format`, `format:check`, and `build` scripts; `build` performs strict TypeScript checking without emitting duplicate browser bundles.

Framework-free packages may emit JavaScript and declarations when they are consumed by Node.js implementations. Desktop bridge contracts are type-only and remain internal to the Desktop module.

## Application Routing

Next.js `page.tsx` files remain in `web/src/app` and `desktop/renderer/src/app`. Each application default-exports or composes the matching page from a domain package, keeping route placement visible without owning domain behavior. Desktop's root layout additionally composes its internal Electron adapter.

## Dependency Direction

```text
@agw/web ────────────────> business packages
@agw/desktop ────────────> business packages
   │                              │
   ├──────────────> @agw/components
   └──────────────> @agw/api ───> @agw/http-client

desktop main/preload/renderer ───> desktop/src/shared/contracts

business packages ───────> @agw/api / @agw/components
@agw/chat ───────────────> execution status and aggregation
```

Domain-to-domain dependencies are allowed only for real shared behavior and must remain acyclic. `@agw/chat` may consume public Agents and Projects interfaces; Jobs may consume the public Agent selector; Settings may consume Auth. Business domains do not depend on Desktop contracts or the Electron adapter. Web and Desktop do not import, locate, build, or depend on each other; each application builds its own output.

## Behavior and Verification

The migration changes ownership, not payloads, storage, API behavior, SignalR behavior, or Electron IPC. Existing tests move with the implementation they exercise. Verification includes frozen installation, package-seam scans, workspace formatting/lint/tests/builds, Desktop static export, and Electron package collection checks.
