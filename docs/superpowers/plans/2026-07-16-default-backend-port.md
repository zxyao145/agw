# Default Backend Port 30815 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make port `30815` the consistent default backend entry point across runtime, development, deployment, and documentation.

**Architecture:** Replace only user-visible and executable default backend port literals. Preserve environment-variable overrides, HTTPS port `7026`, container-internal port `8080`, and arbitrary caller-provided URLs in tests.

**Tech Stack:** ASP.NET Core 10, Next.js 16, JSON, Docker Compose, Caddy, Nginx, Markdown

## Global Constraints

- The portable server fallback is `http://127.0.0.1:30815`.
- Development HTTP launch URLs use port `30815`; HTTPS remains on port `7026`.
- The Next.js fallback is `http://localhost:30815`.
- Docker exposes host port `30815` to container port `8080`.
- `AGENTS.md` and `CLAUDE.md` remain byte-for-byte identical.
- Existing unrelated worktree changes must remain untouched.
- Do not create a Git commit without explicit authorization.

---

### Task 1: Establish the failing default-port contract

**Files:**
- Inspect: `src/server/Agw.Host/Program.cs`
- Inspect: `src/server/Agw.Host/Properties/launchSettings.json`
- Inspect: `src/clients/web/next.config.ts`
- Inspect: `deploy/compose.yaml`

**Interfaces:**
- Consumes: current checked-in default URL literals
- Produces: a reproducible shell contract that fails until every primary default uses `30815`

- [x] **Step 1: Run the new-port assertions before editing**

```bash
test "$(rg -o '127\.0\.0\.1:30815' src/server/Agw.Host/Program.cs | wc -l | tr -d ' ')" = "1" &&
test "$(rg -o '(0\.0\.0\.0|localhost):30815' src/server/Agw.Host/Properties/launchSettings.json | wc -l | tr -d ' ')" = "2" &&
test "$(rg -o 'localhost:30815' src/clients/web/next.config.ts | wc -l | tr -d ' ')" = "2" &&
test "$(rg -o '127\.0\.0\.1:30815:8080' deploy/compose.yaml | wc -l | tr -d ' ')" = "1"
```

Expected: non-zero exit status because the repository still uses port `5015`.

### Task 2: Update executable runtime and deployment defaults

**Files:**
- Modify: `src/server/Agw.Host/Program.cs`
- Modify: `src/server/Agw.Host/Properties/launchSettings.json`
- Modify: `src/clients/web/next.config.ts`
- Modify: `src/clients/web/openapi.json`
- Modify: `deploy/compose.yaml`
- Modify: `deploy/Caddyfile.example`
- Modify: `deploy/nginx.conf.example`

**Interfaces:**
- Consumes: ASP.NET Core URL fallback and launch profiles, Next.js proxy fallback, deployment upstream examples
- Produces: executable defaults that consistently expose the backend on host port `30815`

- [x] **Step 1: Replace runtime URL literals**

Apply these exact substitutions:

```text
Program.cs: http://127.0.0.1:5015 -> http://127.0.0.1:30815
launchSettings.json: HTTP port 5015 -> 30815 in both profiles
next.config.ts: http://localhost:5015 -> http://localhost:30815 in the active and commented configurations
openapi.json: http://localhost:5015/ -> http://localhost:30815/
```

- [x] **Step 2: Replace deployment host and upstream ports**

Apply these exact substitutions while preserving container port `8080`:

```text
deploy/compose.yaml: 127.0.0.1:5015:8080 -> 127.0.0.1:30815:8080
deploy/Caddyfile.example: 127.0.0.1:5015 -> 127.0.0.1:30815
deploy/nginx.conf.example: 127.0.0.1:5015 -> 127.0.0.1:30815
```

### Task 3: Update repository guidance and examples

**Files:**
- Modify: `AGENTS.md`
- Modify: `CLAUDE.md`
- Modify: `README.md`
- Modify: `README.zh-CN.md`
- Modify: `docs/1.Development.md`
- Modify: `docs/4.Deployment.md`
- Modify: `src/server/Agw.Jobs/README.zh-CN.md`
- Modify: `src/server/Agw.Setup/AGENTS.md`

**Interfaces:**
- Consumes: documented backend URLs and proxy/deployment examples
- Produces: documentation that consistently directs users to port `30815`

- [x] **Step 1: Replace documented default and example URLs**

Replace every backend port `5015` reference in the listed files with `30815`. Do not edit mobile or Setup test fixtures whose URLs are explicit caller inputs rather than defaults.

- [x] **Step 2: Verify instruction files remain identical**

```bash
cmp -s AGENTS.md CLAUDE.md
```

Expected: exit status 0.

### Task 4: Verify the completed port change

**Files:**
- Verify: all files modified in Tasks 2 and 3

**Interfaces:**
- Consumes: updated runtime, deployment, and documentation files
- Produces: fresh evidence that the default port is consistent and the repository remains buildable

- [x] **Step 1: Re-run the default-port contract**

```bash
test "$(rg -o '127\.0\.0\.1:30815' src/server/Agw.Host/Program.cs | wc -l | tr -d ' ')" = "1" &&
test "$(rg -o '(0\.0\.0\.0|localhost):30815' src/server/Agw.Host/Properties/launchSettings.json | wc -l | tr -d ' ')" = "2" &&
test "$(rg -o 'localhost:30815' src/clients/web/next.config.ts | wc -l | tr -d ' ')" = "2" &&
test "$(rg -o '127\.0\.0\.1:30815:8080' deploy/compose.yaml | wc -l | tr -d ' ')" = "1"
```

Expected: exit status 0.

- [x] **Step 2: Search for stale semantic URLs outside approved fixtures and design history**

```bash
rg -n '(localhost|127\.0\.0\.1|0\.0\.0\.0):5015' . \
  --glob '!docs/superpowers/**' \
  --glob '!src/clients/mobile/shared/__tests__/**' \
  --glob '!tests/Agw.Setup.Tests/**' \
  --glob '!**/bin/**' --glob '!**/obj/**' --glob '!**/node_modules/**' --glob '!**/.next/**'
```

Expected: exit status 1 with no output.

- [x] **Step 3: Validate structured configuration and formatting**

```bash
jq empty src/server/Agw.Host/Properties/launchSettings.json src/clients/web/openapi.json
git diff --check
cmp -s AGENTS.md CLAUDE.md
```

Expected: all commands exit successfully.

- [x] **Step 4: Build the backend host**

```bash
dotnet build src/server/Agw.Host/Agw.Host.csproj --no-restore
```

Expected: build succeeds with 0 errors. If unrelated dirty-worktree changes prevent the build, report the exact failure without modifying them.

- [x] **Step 5: Review the scoped diff**

```bash
git diff -- src/server/Agw.Host/Program.cs src/server/Agw.Host/Properties/launchSettings.json \
  src/clients/web/next.config.ts src/clients/web/openapi.json deploy/compose.yaml \
  deploy/Caddyfile.example deploy/nginx.conf.example AGENTS.md CLAUDE.md README.md README.zh-CN.md \
  docs/1.Development.md docs/4.Deployment.md src/server/Agw.Jobs/README.zh-CN.md \
  src/server/Agw.Setup/AGENTS.md docs/superpowers/specs/2026-07-16-default-backend-port-design.md \
  docs/superpowers/plans/2026-07-16-default-backend-port.md
```

Expected: every changed line is directly attributable to the requested port update or its approved design/plan record.
