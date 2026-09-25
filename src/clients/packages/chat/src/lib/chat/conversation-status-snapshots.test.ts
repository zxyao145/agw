import assert from "node:assert/strict";
import test from "node:test";

import { ConversationStatusStore, type ConversationStatusSnapshotItem } from "@agw/chat-runtime";
import { ConversationStatusSnapshots } from "./conversation-status-snapshots";

const scope = { serverId: "server-a", projectId: "project-1" };

type PendingRequest = {
  projectId: string;
  resolve: (items: ConversationStatusSnapshotItem[]) => void;
  reject: (error: Error) => void;
};

/** 按调用顺序记录快照请求，由测试决定每个请求的结果。Records snapshot requests so each test settles them. */
function createRequests() {
  const requests: PendingRequest[] = [];
  const load = (projectId: string) =>
    new Promise<readonly ConversationStatusSnapshotItem[]>((resolve, reject) => {
      requests.push({ projectId, resolve, reject });
    });
  return { requests, load };
}

async function settle(): Promise<void> {
  for (let index = 0; index < 5; index += 1) await Promise.resolve();
}

test("triggers during a request add exactly one request after it", async () => {
  const store = new ConversationStatusStore();
  const { requests, load } = createRequests();
  const snapshots = new ConversationStatusSnapshots(store, load, (error) => {
    throw error;
  });

  const first = snapshots.refresh(scope);
  const joined = [snapshots.refresh(scope), snapshots.refresh(scope), snapshots.refresh(scope)];
  assert.equal(requests.length, 1);
  requests[0]!.resolve([{ conversationId: "conversation-1", turnId: "turn-1", status: "running" }]);
  await settle();
  assert.equal(requests.length, 2);
  requests[1]!.resolve([{ conversationId: "conversation-1", turnId: "turn-1", status: "failed" }]);
  await Promise.all([first, ...joined]);

  assert.equal(requests.length, 2);
  assert.equal(store.getStatuses(scope).get("conversation-1"), "failed");
  void snapshots.refresh(scope);
  assert.equal(requests.length, 3, "a trigger after the merged requests finish starts a new one");
});

test("different scopes request independently", () => {
  const store = new ConversationStatusStore();
  const { requests, load } = createRequests();
  const snapshots = new ConversationStatusSnapshots(store, load, () => undefined);

  void snapshots.refresh(scope);
  void snapshots.refresh({ ...scope, projectId: "project-2" });

  assert.deepEqual(
    requests.map((request) => request.projectId),
    ["project-1", "project-2"],
  );
});

test("a failed request keeps existing statuses and reports the error", async () => {
  const store = new ConversationStatusStore();
  store.turnStarted(scope, "conversation-1", "turn-1");
  const { requests, load } = createRequests();
  const errors: unknown[] = [];
  const snapshots = new ConversationStatusSnapshots(store, load, (error) => errors.push(error));

  const refresh = snapshots.refresh(scope);
  requests[0]!.reject(new Error("offline"));
  await refresh;

  assert.equal(errors.length, 1);
  assert.equal(store.getStatuses(scope).get("conversation-1"), "running");
});

test("dispose stops writing results and reporting errors", async () => {
  const store = new ConversationStatusStore();
  const { requests, load } = createRequests();
  const errors: unknown[] = [];
  const snapshots = new ConversationStatusSnapshots(store, load, (error) => errors.push(error));

  const refresh = snapshots.refresh(scope);
  snapshots.dispose();
  requests[0]!.resolve([{ conversationId: "conversation-1", turnId: "turn-1", status: "failed" }]);
  await refresh;

  assert.equal(store.getStatuses(scope).get("conversation-1"), undefined);
  assert.deepEqual(errors, []);
});
