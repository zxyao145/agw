import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";

import { createBearerApiClient, EMPTY_TOKEN_USAGE, type AiMessage } from "@agw/api";
import {
  rawApiResponse,
  replaceExecutionHub,
  startApiServer,
  type ApiServer,
  type TestHubConnection,
  type TestHubOptions,
} from "@agw/test-harness";
import {
  CONVERSATION_DETAILS_PATH,
  ChatHttpClient,
  type ChatConnectionIdentity,
  type ChatConversationScope,
} from "./chat-http-client";
import { ConversationTokenUsageStore } from "./conversation-token-usage";
import type { ExecutionAttachmentStore, ExecutionSession } from "./execution-session";

const scope = { serverId: "server-1", projectId: "project-1", conversationId: "conversation-1" };
const setting = { projectId: "project-1", conversationId: "conversation-1" };
const detailsPath = "/api/projects/project-1/conversations/conversation-1";
const usageStorageKey = `agw:turn-usage:v1:${JSON.stringify(["server-1", "project-1", "conversation-1"])}`;

type MemoryStorage = ExecutionAttachmentStore & { values: Map<string, string> };

function createStorage(): MemoryStorage {
  const values = new Map<string, string>();
  return {
    values,
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => void values.set(key, value),
    removeItem: (key) => void values.delete(key),
  };
}

/** 服务端当前返回的对话详情。The conversation details the server currently returns. */
type DetailsState = { total: number; unsettled: string[]; fail: boolean };

function details(state: DetailsState, conversationId = "conversation-1") {
  return {
    projectId: "project-1",
    conversationId,
    contextId: "context-1",
    title: "Trip",
    executionCount: 1,
    messageCount: 1,
    createTime: "2026-10-23T08:00:00Z",
    usage: {
      inputTokenCount: state.total,
      outputTokenCount: 0,
      totalTokenCount: state.total,
      cachedInputTokenCount: 0,
      reasoningTokenCount: 0,
      unsettledTurnIds: state.unsettled,
    },
    resumeState: null,
  };
}

function detailsRoute(state: DetailsState) {
  return () =>
    state.fail
      ? rawApiResponse({
          status: 500,
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ code: 5000000, title: "Failed" }),
        })
      : details(state);
}

function turnStart(turnId: string, turnSequence = 1): AiMessage {
  return {
    messageId: `${turnId}-start`,
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: {
      type: "agw-turn-start",
      turnId,
      turnSequence,
      conversationId: "conversation-1",
    },
  };
}

function usageMessage(turnId: string, turnSequence: number, ...totals: number[]): AiMessage {
  return {
    messageId: `${turnId}-usage-${turnSequence}`,
    role: "assistant",
    author: "agent",
    contents: totals.map((total) => ({
      type: "UsageContent",
      content: {
        inputTokenCount: total,
        outputTokenCount: 0,
        totalTokenCount: total,
        cachedInputTokenCount: 0,
        reasoningTokenCount: 0,
      },
    })),
    additionalProperties: { turnId, turnSequence },
  };
}

function turnFinished(turnId: string, turnSequence: number): AiMessage {
  return {
    messageId: `${turnId}-finished`,
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: {
      type: "agw-turn-finished",
      status: "completed",
      turnId,
      turnSequence,
      conversationId: "conversation-1",
    },
  };
}

async function waitUntil(condition: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 500 && !condition(); attempt++) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  assert.ok(condition(), "the condition did not hold in time");
}

/** 让已经发出的请求有机会完成。Gives requests already sent a chance to complete. */
async function settle(): Promise<void> {
  for (let attempt = 0; attempt < 50; attempt++) {
    await new Promise((resolve) => setImmediate(resolve));
  }
}

type Harness = {
  state: DetailsState;
  server: ApiServer;
  hubs: TestHubConnection[];
  client: ChatHttpClient;
  store: ConversationTokenUsageStore;
  /** 收到的对话详情请求数。The number of conversation details requests received. */
  detailsRequests(): number;
  /** 已经返回的对话详情响应数。The number of conversation details responses returned. */
  detailsResponses(): number;
  total(key?: ChatConversationScope): number;
  openSession(sessionScope?: ChatConversationScope): Promise<ExecutionSession>;
};

async function createHarness(
  t: TestContext,
  options: {
    provider?: TestHubOptions["provider"];
    storage?: ExecutionAttachmentStore | null;
    onInvoke?: TestHubOptions["onInvoke"];
    state?: DetailsState;
    hubs?: TestHubConnection[];
    beforeStore?: (client: ChatHttpClient) => void;
    /** 第一个对话详情响应在这个 Promise 完成后才返回。The first details response returns only once this promise settles. */
    holdDetails?: Promise<void>;
    /** 模拟刷新页面：沿用同一个 Server 与它的详情状态。Simulates a reload: keeps the same server and its details state. */
    reload?: Harness;
  } = {},
): Promise<Harness> {
  const state = options.reload?.state ?? options.state ?? { total: 0, unsettled: [], fail: false };
  const server =
    options.reload?.server ??
    (await startApiServer({ [`GET ${detailsPath}`]: detailsRoute(state) }));
  const hubs =
    options.hubs ??
    replaceExecutionHub(t, {
      provider: options.provider ?? "InProcess",
      onInvoke: options.onInvoke,
    });
  const storage = options.storage ?? null;
  const api = createBearerApiClient({ baseUrl: server.baseUrl, token: "token-1" });
  let held = options.holdDetails;
  const client = new ChatHttpClient({
    api: {
      ...api,
      apiGet: (async (...args: Parameters<typeof api.apiGet>) => {
        const value = await api.apiGet(...args);
        const hold = held;
        held = undefined;
        if (hold) await hold;
        return value;
      }) as typeof api.apiGet,
    },
    identity: () => ({ baseUrl: server.baseUrl, token: "token-1" }),
    execution: () => ({ baseUrl: server.baseUrl, token: "token-1", attachmentStore: storage }),
  });
  let responses = 0;
  client.subscribe({ kinds: ["response"], channels: ["http"] }, (event) => {
    if (
      event.kind === "response" &&
      event.channel === "http" &&
      event.request.path === CONVERSATION_DETAILS_PATH
    ) {
      responses++;
    }
  });
  options.beforeStore?.(client);
  const store = new ConversationTokenUsageStore(client, storage);
  t.after(() => store.dispose());
  const sessions: ExecutionSession[] = [];
  t.after(async () => {
    for (const session of sessions) await session.dispose();
  });
  return {
    state,
    server,
    hubs,
    client,
    store,
    detailsRequests: () => server.requests.filter((request) => request.path === detailsPath).length,
    detailsResponses: () => responses,
    total: (key = scope) => store.get(key).totalTokenCount,
    async openSession(sessionScope = scope) {
      const session = client.createExecutionSession(sessionScope, { onMessage() {} });
      sessions.push(session);
      await session.configure({
        projectId: sessionScope.projectId,
        conversationId: sessionScope.conversationId,
      });
      return session;
    },
  };
}

function execute(session: ExecutionSession, turnId: string): Promise<void> {
  return session.execute({
    conversationId: "conversation-1",
    agentId: "agent-1",
    agentType: 0,
    turnId,
    input: { messageId: `input-${turnId}`, author: "$agw", contents: [] },
  });
}

function savedTurns(storage: MemoryStorage): unknown {
  const value = storage.values.get(usageStorageKey);
  return value === undefined ? undefined : JSON.parse(value);
}

test("the settled baseline and an unsettled turn's pushed usage count until the turn's sync", async (t) => {
  const storage = createStorage();
  const h = await createHarness(t, { storage, state: { total: 100, unsettled: [], fail: false } });
  await h.openSession();
  await h.client.getConversation(scope);
  assert.equal(h.total(), 100);

  h.hubs[0]!.receive(turnStart("turn-1"));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 7));
  assert.equal(h.total(), 107);
  assert.notEqual(savedTurns(storage), undefined);

  h.state.total = 112;
  h.hubs[0]!.receive(turnFinished("turn-1", 3));
  await waitUntil(() => h.detailsResponses() === 2);

  assert.equal(h.detailsRequests(), 2);
  assert.equal(h.total(), 112);
  assert.equal(savedTurns(storage), undefined);
});

test("details read again during a running turn keep its pushed usage counted", async (t) => {
  const h = await createHarness(t, { state: { total: 100, unsettled: [], fail: false } });
  await h.openSession();
  await h.client.getConversation(scope);
  h.hubs[0]!.receive(turnStart("turn-1"));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 5));

  h.state.unsettled = ["turn-1"];
  await h.client.getConversation(scope);

  assert.equal(h.total(), 105);
});

test("a reloaded store keeps the baseline when replayed usage belongs to a settled turn", async (t) => {
  const h = await createHarness(t, { state: { total: 50, unsettled: [], fail: false } });
  await h.openSession();
  const seen: number[] = [];
  h.store.subscribe(() => seen.push(h.total()));
  await h.client.getConversation(scope);

  h.hubs[0]!.receive(usageMessage("turn-r", 4, 9));
  h.hubs[0]!.receive(turnFinished("turn-r", 5));
  await waitUntil(() => h.detailsResponses() === 2);

  assert.equal(h.total(), 50);
  assert.ok(seen.every((value) => value === 50));
});

test("a replayed start counts the turn until the sync after its finish", async (t) => {
  const h = await createHarness(t, { state: { total: 50, unsettled: [], fail: false } });
  await h.openSession();
  await h.client.getConversation(scope);

  h.hubs[0]!.receive(turnStart("turn-r"));
  h.hubs[0]!.receive(usageMessage("turn-r", 2, 9));
  assert.equal(h.total(), 59);
  h.hubs[0]!.receive(turnFinished("turn-r", 3));
  await waitUntil(() => h.detailsResponses() === 2);

  assert.equal(h.total(), 50);
});

test("a repeated position counts once and every usage content of a message counts", async (t) => {
  const h = await createHarness(t);
  await h.openSession();

  h.hubs[0]!.receive(turnStart("turn-1"));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 3, 4));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 3, 4));
  h.hubs[0]!.receive(usageMessage("turn-1", 3, 1));

  assert.equal(h.total(), 8);
});

for (const failingSubscriber of ["before", "after"] as const) {
  test(`a failed position is counted once after its replay (failing subscriber ${failingSubscriber} the store)`, async (t) => {
    const failing = { turnSequence: 3 as number | null };
    const subscribeFailing = (client: ChatHttpClient) =>
      client.subscribe({ sources: ["live"] }, (event) => {
        if (
          event.kind === "message" &&
          event.message.additionalProperties?.turnSequence === failing.turnSequence
        ) {
          throw new Error("subscriber failed");
        }
      });
    const h = await createHarness(t, {
      provider: "Distributed",
      storage: createStorage(),
      beforeStore: failingSubscriber === "before" ? subscribeFailing : undefined,
    });
    if (failingSubscriber === "after") subscribeFailing(h.client);
    const session = await h.openSession();
    await execute(session, "turn-1");
    const hub = h.hubs[0]!;
    hub.receive(turnStart("turn-1"));
    hub.receive(usageMessage("turn-1", 2, 5));

    assert.throws(() => hub.receive(usageMessage("turn-1", 3, 7)), /subscriber failed/);
    hub.receive(usageMessage("turn-1", 4, 11));
    assert.equal(h.total(), failingSubscriber === "before" ? 5 : 12);

    failing.turnSequence = null;
    hub.reconnect();
    await waitUntil(() => hub.commands("SubscribeTurnCommand").length === 1);
    hub.receive(usageMessage("turn-1", 3, 7));
    hub.receive(usageMessage("turn-1", 4, 11));

    assert.equal(h.total(), 23);
  });
}

test("a reloaded store and session count a saved but unconfirmed position once", async (t) => {
  const storage = createStorage();
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const first = await createHarness(t, { storage, hubs });
  first.client.subscribe({ sources: ["live"] }, (event) => {
    if (event.kind === "message" && event.message.additionalProperties?.turnSequence === 3) {
      throw new Error("subscriber failed");
    }
  });
  const session = await first.openSession();
  await execute(session, "turn-1");
  hubs[0]!.receive(turnStart("turn-1"));
  hubs[0]!.receive(usageMessage("turn-1", 2, 5));
  assert.throws(() => hubs[0]!.receive(usageMessage("turn-1", 3, 7)), /subscriber failed/);
  first.store.dispose();
  await session.dispose();

  const reloaded = await createHarness(t, { storage, hubs, reload: first });
  await reloaded.openSession();
  const replay = hubs.at(-1)!;
  assert.equal(replay.commands("SubscribeTurnCommand")[0]!.cursor, "2");
  replay.receive(usageMessage("turn-1", 3, 7));
  replay.receive(usageMessage("turn-1", 4, 11));

  assert.equal(reloaded.total(), 23);
});

test("a result-only turn without pushed usage takes the synced usage after it finishes", async (t) => {
  const h = await createHarness(t, { state: { total: 100, unsettled: [], fail: false } });
  await h.openSession();
  await h.client.getConversation(scope);
  h.hubs[0]!.receive(turnStart("turn-1"));
  assert.equal(h.total(), 100);

  h.state.total = 130;
  h.hubs[0]!.receive(turnFinished("turn-1", 2));
  await waitUntil(() => h.detailsResponses() === 2);

  assert.equal(h.total(), 130);
});

test("the synced usage includes the summary that was never pushed", async (t) => {
  const h = await createHarness(t, { state: { total: 100, unsettled: [], fail: false } });
  await h.openSession();
  await h.client.getConversation(scope);
  h.hubs[0]!.receive(turnStart("turn-1"));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 10));

  h.state.total = 114;
  h.hubs[0]!.receive(turnFinished("turn-1", 3));
  await waitUntil(() => h.detailsResponses() === 2);

  assert.equal(h.total(), 114);
});

test("of two details responses arriving in reverse order the later request wins", async (t) => {
  const state: DetailsState = { total: 10, unsettled: [], fail: false };
  const server = await startApiServer({ [`GET ${detailsPath}`]: detailsRoute(state) });
  const api = createBearerApiClient({ baseUrl: server.baseUrl, token: "token-1" });
  const releaseFirst = Promise.withResolvers<void>();
  let calls = 0;
  const client = new ChatHttpClient({
    api: {
      ...api,
      apiGet: (async (...args: Parameters<typeof api.apiGet>) => {
        const call = calls++;
        const value = await api.apiGet(...args);
        if (call === 0) await releaseFirst.promise;
        return value;
      }) as typeof api.apiGet,
    },
    identity: () => ({ baseUrl: server.baseUrl, token: "token-1" }),
    execution: () => ({ baseUrl: server.baseUrl, token: "token-1", attachmentStore: null }),
  });
  const store = new ConversationTokenUsageStore(client, null);
  t.after(() => store.dispose());

  const first = client.getConversation(scope);
  await waitUntil(() => server.requests.length === 1);
  state.total = 20;
  await client.getConversation(scope);
  releaseFirst.resolve();
  await first;

  assert.equal(store.get(scope).totalTokenCount, 20);
});

test("failed details and sync requests leave the usage unchanged", async (t) => {
  const h = await createHarness(t, { state: { total: 100, unsettled: [], fail: false } });
  await h.openSession();
  await h.client.getConversation(scope);
  h.hubs[0]!.receive(turnStart("turn-1"));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 5));

  h.state.fail = true;
  await assert.rejects(h.client.getConversation(scope));
  h.hubs[0]!.receive(turnFinished("turn-1", 3));
  await waitUntil(() => h.detailsResponses() === 3);

  assert.equal(h.total(), 105);
});

test("a background turn finishing after a server switch sends no details request until its server returns", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const stateA: DetailsState = { total: 100, unsettled: [], fail: false };
  const stateB: DetailsState = { total: 900, unsettled: [], fail: false };
  const serverA = await startApiServer({ [`GET ${detailsPath}`]: detailsRoute(stateA) });
  const serverB = await startApiServer({ [`GET ${detailsPath}`]: detailsRoute(stateB) });
  const clients = {
    [serverA.baseUrl]: createBearerApiClient({ baseUrl: serverA.baseUrl, token: "token-a" }),
    [serverB.baseUrl]: createBearerApiClient({ baseUrl: serverB.baseUrl, token: "token-b" }),
  };
  let identity: ChatConnectionIdentity = { baseUrl: serverA.baseUrl, token: "token-a" };
  // Desktop 的 apiGet 等函数按当前连接发出请求。Desktop's apiGet and the others send through the current connection.
  const route = (method: "apiGet" | "apiPut" | "apiDelete") =>
    ((...args: unknown[]) =>
      (clients[identity.baseUrl]![method] as (...values: unknown[]) => Promise<unknown>)(
        ...args,
      )) as never;
  const client = new ChatHttpClient({
    api: { apiGet: route("apiGet"), apiPut: route("apiPut"), apiDelete: route("apiDelete") },
    identity: () => identity,
    execution: () => ({ baseUrl: serverA.baseUrl, token: "token-a", attachmentStore: null }),
  });
  const store = new ConversationTokenUsageStore(client, null);
  t.after(() => store.dispose());
  const session = client.createExecutionSession(scope, { onMessage() {} });
  t.after(() => session.dispose());
  await session.configure(setting);
  await client.getConversation(scope);
  const hub = hubs[0]!;
  hub.receive(turnStart("turn-1"));
  hub.receive(usageMessage("turn-1", 2, 5));

  identity = { baseUrl: serverB.baseUrl, token: "token-b" };
  hub.receive(turnFinished("turn-1", 3));
  await settle();

  assert.equal(serverA.requests.length, 1);
  assert.equal(serverB.requests.length, 0);
  assert.equal(store.get(scope).totalTokenCount, 105);

  identity = { baseUrl: serverA.baseUrl, token: "token-a" };
  stateA.total = 108;
  await client.getConversation(scope);
  assert.equal(store.get(scope).totalTokenCount, 108);
});

test("an interrupted turn awaiting settlement keeps counting and syncs after 1, 2, 4, 8 and every 10 seconds", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const h = await createHarness(t, { state: { total: 100, unsettled: [], fail: false } });
  await h.openSession();
  await h.client.getConversation(scope);
  h.hubs[0]!.receive(turnStart("turn-1"));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 5));

  h.state.unsettled = ["turn-1"];
  h.hubs[0]!.receive(turnFinished("turn-1", 3));
  await waitUntil(() => h.detailsResponses() === 2);
  assert.equal(h.total(), 105);

  let responses = 2;
  for (const delay of [1_000, 2_000, 4_000, 8_000, 10_000, 10_000]) {
    t.mock.timers.tick(delay - 1);
    await settle();
    assert.equal(h.detailsRequests(), responses, `no sync before ${delay} ms`);
    t.mock.timers.tick(1);
    responses++;
    await waitUntil(() => h.detailsResponses() === responses);
    assert.equal(h.total(), 105);
  }

  h.state.unsettled = [];
  h.state.total = 109;
  t.mock.timers.tick(10_000);
  responses++;
  await waitUntil(() => h.detailsResponses() === responses);
  assert.equal(h.total(), 109);
  t.mock.timers.tick(60_000);
  await settle();
  assert.equal(h.detailsRequests(), responses);
});

test("a failed settlement sync stops the following syncs", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const h = await createHarness(t, { state: { total: 100, unsettled: [], fail: false } });
  await h.openSession();
  await h.client.getConversation(scope);
  h.hubs[0]!.receive(turnStart("turn-1"));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 5));
  h.state.unsettled = ["turn-1"];
  h.hubs[0]!.receive(turnFinished("turn-1", 3));
  await waitUntil(() => h.detailsResponses() === 2);

  h.state.fail = true;
  t.mock.timers.tick(1_000);
  await waitUntil(() => h.detailsResponses() === 3);
  t.mock.timers.tick(60_000);
  await settle();

  assert.equal(h.detailsRequests(), 3);
  assert.equal(h.total(), 105);
});

test("a reloaded durable turn awaiting approval keeps its pushed usage and counts only new positions", async (t) => {
  const storage = createStorage();
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const state: DetailsState = { total: 100, unsettled: [], fail: false };
  const before = await createHarness(t, { storage, hubs, state });
  const session = await before.openSession();
  await before.client.getConversation(scope);
  await execute(session, "turn-t");
  hubs[0]!.receive(turnStart("turn-t"));
  hubs[0]!.receive(usageMessage("turn-t", 3, 7));
  assert.equal(before.total(), 107);
  before.store.dispose();
  await session.dispose();

  const after = await createHarness(t, { storage, hubs, reload: before });
  state.unsettled = ["turn-t"];
  await after.client.getConversation(scope);
  assert.equal(after.total(), 107);
  await after.openSession();
  const replay = hubs.at(-1)!;
  assert.equal(replay.commands("SubscribeTurnCommand")[0]!.cursor, "3");

  replay.receive(usageMessage("turn-t", 3, 7));
  replay.receive(usageMessage("turn-t", 5, 2));
  assert.equal(after.total(), 109);
  state.unsettled = [];
  state.total = 109;
  replay.receive(turnFinished("turn-t", 6));
  await waitUntil(() => after.detailsResponses() === 2);

  assert.equal(after.total(), 109);
  assert.equal(savedTurns(storage), undefined);
});

test("an in-process reload syncs a restored turn once the previous execution is confirmed idle", async (t) => {
  const storage = createStorage();
  let previousRunning = false;
  const hubs = replaceExecutionHub(t, {
    provider: "InProcess",
    onInvoke: ({ method }) => {
      if (method === "FindInProcessExecution") return previousRunning ? "connection-1" : undefined;
    },
  });
  const state: DetailsState = { total: 100, unsettled: [], fail: false };
  const before = await createHarness(t, { storage, hubs, state });
  await before.openSession();
  await before.client.getConversation(scope);
  hubs[0]!.receive(turnStart("turn-1"));
  hubs[0]!.receive(usageMessage("turn-1", 2, 6));
  before.store.dispose();

  previousRunning = true;
  state.unsettled = ["turn-1"];
  const after = await createHarness(t, { storage, hubs, reload: before });
  await after.client.getConversation(scope);
  assert.equal(after.total(), 106);
  state.unsettled = [];
  state.total = 106;
  await after.openSession();
  await waitUntil(() => after.detailsResponses() === 2);

  assert.equal(after.total(), 106);
  assert.equal(savedTurns(storage), undefined);
});

test("an in-process reload without pushed usage syncs the listed turn once the execution is idle", async (t) => {
  const hubs = replaceExecutionHub(t, {
    provider: "InProcess",
    onInvoke: ({ method }) => (method === "FindInProcessExecution" ? "connection-1" : undefined),
  });
  const state: DetailsState = { total: 100, unsettled: ["turn-1"], fail: false };
  const h = await createHarness(t, { hubs, state, storage: createStorage() });
  await h.client.getConversation(scope);
  assert.equal(h.total(), 100);

  state.unsettled = [];
  state.total = 130;
  await h.openSession();
  await waitUntil(() => h.detailsResponses() === 2);

  assert.equal(h.total(), 130);
});

test("an idle confirmation syncs only a baseline with unsettled turns, in either order, and only once", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const settledState: DetailsState = { total: 100, unsettled: [], fail: false };
  const settled = await createHarness(t, { hubs, state: settledState });
  await settled.client.getConversation(scope);
  await settled.openSession();
  await settle();
  assert.equal(settled.detailsRequests(), 1);
  settled.store.dispose();

  const unsettledState: DetailsState = { total: 100, unsettled: ["turn-1"], fail: false };
  const baselineFirst = await createHarness(t, { hubs, state: unsettledState });
  await baselineFirst.client.getConversation(scope);
  await baselineFirst.openSession();
  await waitUntil(() => baselineFirst.detailsResponses() === 2);
  await settle();
  assert.equal(baselineFirst.detailsRequests(), 2);
  baselineFirst.store.dispose();

  // 详情请求在确认之前发出，响应在确认之后到达。The details request is sent before the confirmation and answered after it.
  const releaseDetails = Promise.withResolvers<void>();
  const confirmationFirst = await createHarness(t, {
    hubs,
    state: unsettledState,
    holdDetails: releaseDetails.promise,
  });
  const reading = confirmationFirst.client.getConversation(scope);
  await waitUntil(() => confirmationFirst.detailsRequests() === 1);
  await confirmationFirst.openSession();
  assert.equal(confirmationFirst.detailsResponses(), 0);
  releaseDetails.resolve();
  await reading;
  await waitUntil(() => confirmationFirst.detailsResponses() === 2);
  await settle();
  assert.equal(confirmationFirst.detailsRequests(), 2);
});

test("storage failures leave the counting working", async (t) => {
  const failing: ExecutionAttachmentStore = {
    getItem: () => {
      throw new Error("storage unavailable");
    },
    setItem: () => {
      throw new Error("storage unavailable");
    },
    removeItem: () => {
      throw new Error("storage unavailable");
    },
  };
  const h = await createHarness(t, {
    storage: failing,
    state: { total: 100, unsettled: [], fail: false },
  });
  await h.openSession();
  await h.client.getConversation(scope);

  h.hubs[0]!.receive(turnStart("turn-1"));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 5));
  assert.equal(h.total(), 105);
  h.state.total = 105;
  h.hubs[0]!.receive(turnFinished("turn-1", 3));
  await waitUntil(() => h.detailsResponses() === 2);

  assert.equal(h.total(), 105);
});

test("usage content without a turn position fails its subscriber", async (t) => {
  const h = await createHarness(t);
  await h.openSession();
  const withoutSequence = usageMessage("turn-1", 2, 5);
  withoutSequence.additionalProperties = { turnId: "turn-1" };
  const withoutTurn = usageMessage("turn-1", 2, 5);
  withoutTurn.additionalProperties = { turnSequence: 2 };

  assert.throws(() => h.hubs[0]!.receive(withoutSequence), /turnId and turnSequence/);
  assert.throws(() => h.hubs[0]!.receive(withoutTurn), /turnId and turnSequence/);
});

test("a new conversation without a baseline counts every turn's pushed usage", async (t) => {
  const h = await createHarness(t);
  await h.openSession();

  h.hubs[0]!.receive(turnStart("turn-1"));
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 3));
  h.hubs[0]!.receive(turnStart("turn-2"));
  h.hubs[0]!.receive(usageMessage("turn-2", 2, 4));

  assert.equal(h.total(), 7);
});

test("conversations and servers keep separate usage, and history usage is not counted", async (t) => {
  const h = await createHarness(t);
  const other = { ...scope, conversationId: "conversation-2" };
  const otherServer = { ...scope, serverId: "server-2" };
  await h.openSession();
  await h.openSession(other);
  await h.openSession(otherServer);
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 3));
  h.hubs[1]!.receive(usageMessage("turn-2", 2, 5));
  h.hubs[2]!.receive(usageMessage("turn-3", 2, 11));
  const history = await startApiServer({
    [`GET ${detailsPath}/messages`]: {
      items: [usageMessage("turn-1", 9, 100)],
      nextCursor: null,
      hasMore: false,
      turns: [],
    },
  });
  const historyClient = new ChatHttpClient({
    api: createBearerApiClient({ baseUrl: history.baseUrl, token: "token-1" }),
    identity: () => ({ baseUrl: history.baseUrl, token: "token-1" }),
    execution: () => ({ baseUrl: history.baseUrl, token: "token-1", attachmentStore: null }),
  });
  const historyStore = new ConversationTokenUsageStore(historyClient, null);
  t.after(() => historyStore.dispose());
  await historyClient.getConversationMessages(scope, { direction: "older" });

  assert.equal(h.total(), 3);
  assert.equal(h.total(other), 5);
  assert.equal(h.total(otherServer), 11);
  assert.equal(historyStore.get(scope), EMPTY_TOKEN_USAGE);
});

test("get returns the empty usage, a stable object while unchanged, and nothing changes after dispose", async (t) => {
  const h = await createHarness(t);
  await h.openSession();
  assert.equal(h.store.get(scope), EMPTY_TOKEN_USAGE);
  let notifications = 0;
  h.store.subscribe(() => notifications++);

  h.hubs[0]!.receive(usageMessage("turn-1", 2, 3));
  const counted = h.store.get(scope);
  h.hubs[0]!.receive(usageMessage("turn-1", 2, 3));

  assert.equal(notifications, 1);
  assert.equal(h.store.get(scope), counted);
  h.store.dispose();
  h.hubs[0]!.receive(usageMessage("turn-1", 3, 4));
  assert.equal(notifications, 1);
  assert.equal(h.store.get(scope).totalTokenCount, 3);
});
