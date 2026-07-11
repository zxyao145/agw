# Mobile API Data Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace mock data under `mobile/shared/src/rn/pages/home` with data loaded from the Agw backend.

**Architecture:** Add a small React Native API client that uses the saved mobile config (`serverDomain`, `apiKey`) and unwraps Bens.Results envelopes. Keep view components presentational and let `AgwMobilePage` own loading projects, targets, task history, messages, files, and message execution.

**Tech Stack:** Expo React Native, TypeScript, Jest, backend endpoints described by `mobile/agw.json`.

---

### Task 1: API Client

**Files:**
- Create: `mobile/shared/src/rn/api/agw-api-client.ts`
- Test: `mobile/shared/__tests__/agw-api-client.test.ts`

- [ ] **Step 1: Write the failing test**

Cover base URL joining, `X-API-Key`, query strings, JSON request bodies, Bens.Results unwrap, raw file response fallback, and HTTP errors.

- [ ] **Step 2: Run test to verify it fails**

Run: `npm test -- agw-api-client.test.ts --runInBand`
Expected: fail because `src/rn/api/agw-api-client.ts` does not exist.

- [ ] **Step 3: Write minimal implementation**

Implement `createAgwApiClient(config)` with `getJson`, `postJson`, `deleteJson`, and `getText`.

- [ ] **Step 4: Run test to verify it passes**

Run: `npm test -- agw-api-client.test.ts --runInBand`
Expected: pass.

### Task 2: Home Data Loading

**Files:**
- Modify: `mobile/shared/src/rn/pages/home/AgwMobilePage.tsx`
- Modify: `mobile/shared/src/rn/pages/home/components/chat-panel.tsx`
- Modify: `mobile/shared/src/rn/pages/home/components/history-drawer.tsx`
- Modify: `mobile/shared/src/rn/pages/home/components/files-panel.tsx`
- Modify: `mobile/shared/src/rn/pages/home/components/composer.tsx`
- Test: `mobile/shared/__tests__/App.test.tsx`

- [ ] **Step 1: Write the failing tests**

Update `App.test.tsx` so successful API responses render project names, task titles, task message text, and file names from mocked backend responses. Assert old mock-only strings such as `Sarah is typing` and `Brand_Assets_Hero.png` are absent after the API data loads.

- [ ] **Step 2: Run test to verify it fails**

Run: `npm test -- App.test.tsx --runInBand`
Expected: fail because components still render hard-coded mock data and do not call `fetch`.

- [ ] **Step 3: Write minimal implementation**

Load `/api/projects`, `/api/agents`, `/api/agentflows`, `/api/projects/{projectId}/tasks`, `/api/projects/{projectId}/tasks/{taskId}`, and `/api/files/list` from `AgwMobilePage`. Pass normalized props into the presentational panels.

- [ ] **Step 4: Run test to verify it passes**

Run: `npm test -- App.test.tsx --runInBand`
Expected: pass.

### Task 3: Message Execution

**Files:**
- Modify: `mobile/shared/src/rn/pages/home/AgwMobilePage.tsx`
- Modify: `mobile/shared/src/rn/pages/home/components/composer.tsx`
- Test: `mobile/shared/__tests__/App.test.tsx`

- [ ] **Step 1: Write the failing test**

Add a test that types a message, presses send, and expects a POST to `/api/executions/{id}/execute` with selected project/task/target metadata and `X-API-Key`. Assert returned assistant text appears in the chat.

- [ ] **Step 2: Run test to verify it fails**

Run: `npm test -- App.test.tsx --runInBand`
Expected: fail because send is not wired.

- [ ] **Step 3: Write minimal implementation**

Wire composer text state and send handler. Use HTTP execute for mobile, append the local user message immediately, then replace/append returned server messages and refresh history.

- [ ] **Step 4: Run test to verify it passes**

Run: `npm test -- App.test.tsx --runInBand`
Expected: pass.

### Task 4: Verification

**Files:**
- No new files.

- [ ] **Step 1: Run focused tests**

Run: `npm test -- agw-api-client.test.ts App.test.tsx --runInBand`
Expected: pass.

- [ ] **Step 2: Run typecheck**

Run: `npm run typecheck`
Expected: pass.

- [ ] **Step 3: Inspect diff**

Run: `git diff -- mobile`
Expected: only mobile API replacement files and tests changed.
