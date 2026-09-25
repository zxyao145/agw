import assert from "node:assert/strict";
import test from "node:test";

import { ConversationStatusStore, type ConversationStatus } from "./conversation-status-store";

const scope = { serverId: "server-a", projectId: "project-1" };

function statusOf(store: ConversationStatusStore, conversationId: string): ConversationStatus {
  return store.getStatuses(scope).get(conversationId) ?? "idle";
}

test("a snapshot initializes every non-idle status and leaves other conversations idle", () => {
  const store = new ConversationStatusStore();

  store.applySnapshot(scope, store.beginSnapshot(scope), [
    { conversationId: "running", turnId: "turn-1", status: "running" },
    { conversationId: "failed", turnId: "turn-2", status: "failed" },
    { conversationId: "interrupted", turnId: "turn-3", status: "interrupted" },
  ]);

  assert.equal(statusOf(store, "running"), "running");
  assert.equal(statusOf(store, "failed"), "failed");
  assert.equal(statusOf(store, "interrupted"), "interrupted");
  assert.equal(statusOf(store, "other"), "idle");
  assert.equal(store.get(scope, "failed")?.turnId, "turn-2");
});

test("a turn start sets running and each finish result maps to its status", () => {
  const store = new ConversationStatusStore();

  store.turnStarted(scope, "conversation-1", "turn-1");
  const running = statusOf(store, "conversation-1");
  const finished = (["completed", "failed", "interrupted"] as const).map((result, index) => {
    const turnId = `turn-${index + 2}`;
    store.turnStarted(scope, "conversation-1", turnId);
    store.turnFinished(scope, "conversation-1", turnId, result);
    return statusOf(store, "conversation-1");
  });

  assert.equal(running, "running");
  assert.deepEqual(finished, ["idle", "failed", "interrupted"]);
  assert.equal(store.get(scope, "conversation-1")?.turnId, "turn-4");
});

test("a snapshot keeps an event received while its request was in flight", () => {
  const store = new ConversationStatusStore();
  const requestRevision = store.beginSnapshot(scope);

  store.turnStarted(scope, "conversation-1", "turn-1");
  store.applySnapshot(scope, requestRevision, [
    { conversationId: "conversation-2", turnId: "turn-2", status: "failed" },
  ]);

  assert.equal(statusOf(store, "conversation-1"), "running");
  assert.equal(statusOf(store, "conversation-2"), "failed");
});

test("an earlier snapshot returning after a later one keeps the later result", () => {
  const store = new ConversationStatusStore();
  const earlier = store.beginSnapshot(scope);
  const later = store.beginSnapshot(scope);

  store.applySnapshot(scope, later, [
    { conversationId: "conversation-1", turnId: "turn-2", status: "failed" },
  ]);
  store.applySnapshot(scope, earlier, [
    { conversationId: "conversation-1", turnId: "turn-1", status: "running" },
    { conversationId: "conversation-2", turnId: "turn-3", status: "running" },
  ]);

  assert.equal(statusOf(store, "conversation-1"), "failed");
  assert.equal(store.get(scope, "conversation-1")?.turnId, "turn-2");
  assert.equal(store.get(scope, "conversation-2"), undefined);
});

test("snapshots applied in request order let the later one replace the earlier result", () => {
  const store = new ConversationStatusStore();
  const earlier = store.beginSnapshot(scope);
  const later = store.beginSnapshot(scope);

  store.applySnapshot(scope, earlier, [
    { conversationId: "conversation-1", turnId: "turn-1", status: "running" },
  ]);
  store.applySnapshot(scope, later, [
    { conversationId: "conversation-1", turnId: "turn-1", status: "interrupted" },
  ]);

  assert.equal(statusOf(store, "conversation-1"), "interrupted");
});

test("a local record missing from the snapshot becomes idle and keeps its turn", () => {
  const store = new ConversationStatusStore();
  store.turnStarted(scope, "stale", "turn-1");
  const requestRevision = store.beginSnapshot(scope);
  store.turnStarted(scope, "updated", "turn-2");

  store.applySnapshot(scope, requestRevision, []);

  assert.deepEqual(store.get(scope, "stale"), {
    turnId: "turn-1",
    status: "idle",
    revision: requestRevision,
  });
  assert.equal(statusOf(store, "updated"), "running");
});

test("an old turn's finish cannot overwrite the newer running turn", () => {
  const store = new ConversationStatusStore();

  store.turnStarted(scope, "conversation-1", "turn-1");
  store.turnStarted(scope, "conversation-1", "turn-2");
  store.turnFinished(scope, "conversation-1", "turn-1", "failed");

  assert.equal(statusOf(store, "conversation-1"), "running");
  assert.equal(store.get(scope, "conversation-1")?.turnId, "turn-2");
});

test("repeated start and finish messages keep the finished result", () => {
  const store = new ConversationStatusStore();

  store.turnStarted(scope, "conversation-1", "turn-1");
  store.turnStarted(scope, "conversation-1", "turn-1");
  store.turnFinished(scope, "conversation-1", "turn-1", "failed");
  store.turnStarted(scope, "conversation-1", "turn-1");
  store.turnFinished(scope, "conversation-1", "turn-1", "failed");

  assert.equal(statusOf(store, "conversation-1"), "failed");
});

test("reset clears to idle while remove and removeScope drop records", () => {
  const store = new ConversationStatusStore();
  store.applySnapshot(scope, store.beginSnapshot(scope), [
    { conversationId: "cleared", turnId: "turn-1", status: "failed" },
    { conversationId: "deleted", turnId: "turn-2", status: "interrupted" },
    { conversationId: "kept", turnId: "turn-3", status: "running" },
  ]);
  const inFlight = store.beginSnapshot(scope);

  store.reset(scope, "cleared");
  store.remove(scope, "deleted");
  store.applySnapshot(scope, inFlight, [
    { conversationId: "cleared", turnId: "turn-1", status: "failed" },
    { conversationId: "deleted", turnId: "turn-2", status: "interrupted" },
    { conversationId: "kept", turnId: "turn-3", status: "running" },
  ]);

  assert.equal(store.get(scope, "cleared")?.turnId, null);
  assert.equal(statusOf(store, "cleared"), "idle", "an in-flight snapshot cannot restore it");
  assert.equal(store.get(scope, "deleted"), undefined, "an in-flight snapshot cannot restore it");
  assert.equal(statusOf(store, "kept"), "running");

  const beforeRemoval = store.beginSnapshot(scope);
  store.removeScope(scope);
  store.applySnapshot(scope, beforeRemoval, [
    { conversationId: "kept", turnId: "turn-3", status: "running" },
  ]);
  assert.equal(store.getStatuses(scope).size, 0);
});

test("the same conversation ID is isolated by server and project", () => {
  const store = new ConversationStatusStore();
  const otherServer = { serverId: "server-b", projectId: "project-1" };
  const otherProject = { serverId: "server-a", projectId: "project-2" };

  store.turnStarted(scope, "conversation-1", "turn-1");
  store.applySnapshot(otherServer, store.beginSnapshot(otherServer), [
    { conversationId: "conversation-1", turnId: "turn-2", status: "failed" },
  ]);
  store.applySnapshot(otherProject, store.beginSnapshot(otherProject), []);

  assert.equal(statusOf(store, "conversation-1"), "running");
  assert.equal(store.getStatuses(otherServer).get("conversation-1"), "failed");
  assert.equal(store.getStatuses(otherProject).get("conversation-1"), undefined);
});

test("the status map keeps its identity until the scope is written", () => {
  const store = new ConversationStatusStore();
  let notifications = 0;
  store.subscribe(() => {
    notifications += 1;
  });
  store.turnStarted(scope, "conversation-1", "turn-1");
  const first = store.getStatuses(scope);

  store.turnStarted({ serverId: "server-a", projectId: "project-2" }, "conversation-1", "turn-2");
  const unchanged = store.getStatuses(scope);
  store.turnFinished(scope, "conversation-1", "turn-1", "completed");

  assert.equal(unchanged, first);
  assert.notEqual(store.getStatuses(scope), first);
  assert.equal(notifications, 3);
});
