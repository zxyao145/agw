import assert from "node:assert/strict";
import test from "node:test";

// @ts-expect-error Node's type stripping requires the explicit TypeScript extension.
import { ExecutionActivityStore } from "./execution-activity-store.ts";

test("detaching a running project keeps it active and records unread completion", () => {
  const store = new ExecutionActivityStore();
  const key = { serverId: "local", projectId: "project-1", contextId: "context-1" };

  store.attach(key);
  store.turnStarted(key);
  store.detach(key);
  assert.equal(store.getProjectStatus("local", "project-1"), "running");
  assert.equal(store.getActiveCount(), 1);

  store.turnFinished(key, "completed");
  assert.equal(store.getProjectStatus("local", "project-1"), "completed-unread");

  store.attach(key);
  assert.equal(store.getProjectStatus("local", "project-1"), "idle");
});

test("project aggregation prioritizes approval, failure, and running tasks", () => {
  const store = new ExecutionActivityStore();
  const first = { serverId: "local", projectId: "project-1", contextId: "first" };
  const second = { serverId: "local", projectId: "project-1", contextId: "second" };

  store.turnStarted(first);
  store.turnStarted(second);
  store.connectionClosed(first, new Error("connection lost"));
  assert.equal(store.getProjectStatus("local", "project-1"), "failed-unread");

  store.waitingForApproval(second);
  assert.equal(store.getProjectStatus("local", "project-1"), "waiting-approval");
});
