# Client Monorepo Directory Structure

Web and Desktop are independent applications with separate Next.js route shells. They do not import, build, locate, or consume artifacts from each other; reusable infrastructure and business modules live under root `packages`.

```text
clients/
├── web/                              # @agw/web: browser Next.js application
│   └── src/
│       └── app/                      # thin browser routes, layouts, CSS, shell composition
│           ├── login/page.tsx        # imports @agw/auth
│           ├── (app)/
│           │   ├── (agents)/         # imports @agw/agents
│           │   ├── (interface)/chat/ # imports @agw/chat
│           │   ├── (jobs)/           # imports @agw/jobs
│           │   ├── (overview)/       # imports @agw/observability
│           │   ├── (providers)/      # imports @agw/providers
│           │   ├── (tasks)/          # imports @agw/projects
│           │   ├── integrations/     # imports @agw/integrations
│           │   ├── settings/         # imports @agw/settings
│           │   └── skills/           # imports @agw/skills
│           └── layout.tsx            # composes browser providers
│
├── desktop/                          # @agw/desktop: independent Electron application
│   ├── renderer/                     # Desktop-owned Next.js React renderer
│   │   └── src/
│   │       ├── app/                  # Desktop route shell importing @agw/* packages
│   │       └── runtime/              # Electron bridge adapter, shell, connection UI
│   └── src/
│       ├── main/                     # Electron main process
│       ├── preload/                  # whitelisted context bridge
│       └── shared/contracts/         # internal cross-process data shapes
│
├── packages/
│   ├── http-client/                  # HTTP primitives, response parsing, transport errors
│   ├── api/                          # generated OpenAPI types and typed API runtime
│   ├── components/                   # shared React UI and design tokens
│   │   └── src/
│   │       ├── ui-web/
│   │       │   └── shadcn/
│   │       └── ui-tokens/
│   ├── auth/                         # authentication state and Web UI
│   ├── agents/                       # Agents and Agentflows
│   ├── projects/                     # Projects, tasks, histories, and file explorer
│   ├── chat/                         # Chat domain, execution state, SignalR, reusable Chat UI
│   ├── providers/                    # Providers and models
│   ├── integrations/                 # plugins, connections, shared capabilities
│   ├── jobs/                         # scheduled jobs and logs
│   ├── skills/                       # skill management
│   ├── observability/                # dashboard and traces
│   └── settings/                     # Server and account settings
│
├── tools/
│   └── scripts/
│       └── check-client-boundaries.mjs
├── package.json
├── pnpm-workspace.yaml
├── pnpm-lock.yaml
├── turbo.json
├── tsconfig.json
└── tsconfig.react.json
```

Domain packages use this shape as needed:

```text
packages/<domain>/
├── src/
│   ├── schemas/          # optional validation schemas
│   ├── types/            # domain-owned TypeScript types
│   ├── lib/              # platform-neutral pure TypeScript helpers
│   ├── hooks/            # reusable React hooks
│   ├── services/         # application workflows and business coordination
│   ├── adapters/         # optional platform boundaries
│   ├── state/            # domain state stores
│   ├── ui-web/           # React pages/components shared by Web and Electron
│   │   ├── pages/
│   │   └── components/
│   └── index.ts          # public package surface
├── package.json
└── tsconfig.json
```

Rules:

- `web/src/app` imports public `@agw/*` package entry points; business implementations do not live in Web.
- Packages never import `@agw/web`, `web/src`, or Web's `@/` alias.
- Web and Desktop never import, locate, build, or depend on each other; both resolve workspace dependencies through root `packages`.
- `desktop/renderer` is part of `@agw/desktop`, not a separate workspace package. It imports public `@agw/*` package entry points through its own route shell.
- Desktop builds and packages its own static renderer export. No root tool assembles Desktop from Web artifacts.
- Electron bridge contracts remain internal to `desktop/src/shared/contracts`; business packages do not depend on them.
- Mobile continues to use its existing npm workflow and is not a member of this workspace.
