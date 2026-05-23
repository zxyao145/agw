# Mobile Chat Message Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render mobile chat messages with Markdown and web-aligned message grouping.

**Architecture:** Keep the change local to the mobile chat panel. Add a React Native Markdown renderer, process messages before rendering, and render each message through content-type grouped nodes.

**Tech Stack:** React Native, Expo, Jest, `react-test-renderer`, `react-native-markdown-display`, TypeScript.

---

## File Structure

- Modify `mobile/shared/package.json` and `mobile/shared/package-lock.json`: add `react-native-markdown-display`.
- Create `mobile/shared/__tests__/chat-panel.test.tsx`: focused component tests for filtering, grouping, Markdown, and JSON formatting.
- Modify `mobile/shared/src/rn/pages/home/components/chat-panel.tsx`: add message processing, Markdown rendering, content grouping, and tool group rendering.
- Modify `mobile/shared/src/rn/pages/home/components/styles.ts`: add styles for Markdown nodes, message titles, and tool groups.

## Task 1: Add Chat Panel Behavior Tests

**Files:**
- Create: `mobile/shared/__tests__/chat-panel.test.tsx`

- [ ] **Step 1: Write the failing tests**

Create tests that mock `react-native-markdown-display`, render `ChatPanel`, and assert:

- Markdown text reaches the Markdown component.
- `system` messages and messages without `author` are not rendered.
- A function call and matching function result render under one `Tool use` group.
- JSON function content is formatted as a fenced JSON code block.

- [ ] **Step 2: Run tests to verify failure**

Run: `npm test -- --runTestsByPath __tests__/chat-panel.test.tsx`

Expected: FAIL because current `chat-panel.tsx` renders plain `Text` nodes and does not group function messages.

## Task 2: Add Markdown Dependency

**Files:**
- Modify: `mobile/shared/package.json`
- Modify: `mobile/shared/package-lock.json`

- [ ] **Step 1: Install dependency**

Run: `npm install react-native-markdown-display`

Expected: package and lockfile include `react-native-markdown-display`.

## Task 3: Implement Message Processing And Rendering

**Files:**
- Modify: `mobile/shared/src/rn/pages/home/components/chat-panel.tsx`
- Modify: `mobile/shared/src/rn/pages/home/components/styles.ts`

- [ ] **Step 1: Implement processing helpers**

Add local helpers for `processMessages`, `groupContentsByType`, `buildContentNode`, `isTextNode`, `getNodePrefix`, and `stripCommandTags`.

- [ ] **Step 2: Replace row renderer**

Render normal processed items with `AgwMessageComponent` and function groups with a compact mobile group header plus nested message components.

- [ ] **Step 3: Render Markdown nodes**

Use `react-native-markdown-display` for text-like nodes and provide mobile text/code/list styles from `styles.ts`.

- [ ] **Step 4: Run tests**

Run: `npm test -- --runTestsByPath __tests__/chat-panel.test.tsx`

Expected: PASS.

## Task 4: Verify Mobile Package

**Files:**
- No additional file changes expected.

- [ ] **Step 1: Run typecheck**

Run: `npm run typecheck`

Expected: PASS with no TypeScript errors.

- [ ] **Step 2: Run targeted app tests if needed**

Run: `npm test -- --runTestsByPath __tests__/App.test.tsx`

Expected: PASS, confirming the home page still renders API chat history.
