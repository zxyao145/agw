import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { window } = await setupDomEnvironment();
const { conversationComposerStorage } = await import("./conversation-composer-storage.ts");

const scope = { serverId: "server-a", projectId: "project-1" };

beforeEach(() => {
  window.localStorage.clear();
});

test("each conversation merges its own target and draft", () => {
  conversationComposerStorage.set(scope, "conversation-1", { targetValue: "agent:codex" });
  conversationComposerStorage.set(scope, "conversation-1", { input: "draft" });
  conversationComposerStorage.set(scope, null, { input: "new draft" });

  assert.deepEqual(conversationComposerStorage.get(scope, "conversation-1"), {
    targetValue: "agent:codex",
    input: "draft",
  });
  assert.deepEqual(conversationComposerStorage.get(scope, null), { input: "new draft" });
  assert.deepEqual(conversationComposerStorage.get(scope, "conversation-2"), {});
});

test("another server or project keeps separate values", () => {
  conversationComposerStorage.set(scope, null, { targetValue: "agent:codex" });

  assert.deepEqual(conversationComposerStorage.get({ ...scope, serverId: "server-b" }, null), {});
  assert.deepEqual(conversationComposerStorage.get({ ...scope, projectId: "project-2" }, null), {});
});

test("an entry is removed once both fields are empty", () => {
  conversationComposerStorage.set(scope, "conversation-1", {
    targetValue: "agent:codex",
    input: "x",
  });
  conversationComposerStorage.set(scope, "conversation-1", { input: "" });
  assert.deepEqual(conversationComposerStorage.get(scope, "conversation-1"), {
    targetValue: "agent:codex",
  });

  conversationComposerStorage.set(scope, "conversation-1", { targetValue: undefined });

  assert.equal(window.localStorage.length, 0);
});

test("clearing a project's conversations keeps the new conversation and other scopes", () => {
  const otherProject = { ...scope, projectId: "project-2" };
  const otherServer = { ...scope, serverId: "server-b" };
  conversationComposerStorage.set(scope, "conversation-1", { input: "one" });
  conversationComposerStorage.set(scope, "conversation-2", { input: "two" });
  conversationComposerStorage.set(scope, null, { input: "new" });
  conversationComposerStorage.set(otherProject, "conversation-3", { input: "three" });
  conversationComposerStorage.set(otherServer, "conversation-1", { input: "other server" });

  conversationComposerStorage.removeConversations(scope);

  assert.deepEqual(conversationComposerStorage.get(scope, "conversation-1"), {});
  assert.deepEqual(conversationComposerStorage.get(scope, "conversation-2"), {});
  assert.deepEqual(conversationComposerStorage.get(scope, null), { input: "new" });
  assert.deepEqual(conversationComposerStorage.get(otherProject, "conversation-3"), {
    input: "three",
  });
  assert.deepEqual(conversationComposerStorage.get(otherServer, "conversation-1"), {
    input: "other server",
  });
});
