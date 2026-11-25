import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";
import {
  conversationComposerStorage as storage,
  getStorageKey,
} from "./conversation-composer-storage";

const { window } = await setupDomEnvironment();
const scope = { serverId: "server-a", projectId: "project-1" };
const draft = { draftId: "d1" };
const conversation = { conversationId: "c1" };
beforeEach(() => window.localStorage.clear());

test("clearing Web conversations preserves its new-conversation composer", () => {
  storage.set(scope, { draftId: "web" }, { input: "next", targetValue: "agent:codex" });
  storage.set(scope, conversation, { input: "past" });
  storage.removeConversations(scope, { draftId: "web" });
  assert.deepEqual(storage.get(scope, { draftId: "web" }), {
    input: "next",
    targetValue: "agent:codex",
  });
  assert.deepEqual(storage.get(scope, conversation), {});
});

test("draft and conversation keys are separate and move transfers the values", () => {
  assert.match(getStorageKey(scope, draft), /draft:d1$/);
  assert.match(getStorageKey(scope, conversation), /conversation:c1$/);
  storage.set(scope, draft, { input: "hello" });
  assert.deepEqual(storage.get(scope, { draftId: "d2" }), {});
  storage.move(scope, draft, conversation);
  assert.deepEqual(storage.get(scope, draft), {});
  assert.deepEqual(storage.get(scope, conversation), { input: "hello" });
});

test("each conversation merges its own target and draft and removes empty entries", () => {
  storage.set(scope, conversation, { targetValue: "agent:codex" });
  storage.set(scope, conversation, { input: "draft" });
  assert.deepEqual(storage.get(scope, conversation), {
    targetValue: "agent:codex",
    input: "draft",
  });
  storage.set(scope, conversation, { input: "" });
  assert.deepEqual(storage.get(scope, conversation), { targetValue: "agent:codex" });
  storage.set(scope, conversation, { targetValue: undefined });
  assert.equal(window.localStorage.length, 0);
});

test("clearing a project's conversations removes its drafts and preserves other scopes", () => {
  const otherProject = { ...scope, projectId: "project-2" };
  const otherServer = { ...scope, serverId: "server-b" };
  storage.set(scope, conversation, { input: "one" });
  storage.set(scope, draft, { input: "two" });
  storage.set(otherProject, conversation, { input: "three" });
  storage.set(otherServer, draft, { input: "four" });
  storage.removeConversations(scope);
  assert.deepEqual(storage.get(scope, conversation), {});
  assert.deepEqual(storage.get(scope, draft), {});
  assert.deepEqual(storage.get(otherProject, conversation), { input: "three" });
  assert.deepEqual(storage.get(otherServer, draft), { input: "four" });
});
