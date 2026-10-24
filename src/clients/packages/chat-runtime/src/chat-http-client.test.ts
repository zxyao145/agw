import assert from "node:assert/strict";
import test from "node:test";

import { ApiTransportError, createBearerApiClient, type AiMessage } from "@agw/api";
import type { ChatEvent, ChatEventFilter } from "@agw/execution-core";
import {
  rawApiResponse,
  replaceExecutionHub,
  startApiServer,
  type ApiRequestRecord,
  type ApiRoutes,
  type ApiServer,
} from "@agw/test-harness";
import {
  CONVERSATION_DETAILS_PATH,
  ChatHttpClient,
  type ChatConnectionIdentity,
} from "./chat-http-client";
import {
  getDurableExecutionStorageKey,
  type ExecutionAttachmentStore,
  type ExecutionRuntimeConfig,
} from "./execution-session";

const scope = { serverId: "server-1", projectId: "project-1", conversationId: "conversation-1" };
const setting = { projectId: "project-1", conversationId: "conversation-1" };
const conversationPath = "/api/projects/project-1/conversations/conversation-1";

function conversationDetails() {
  return {
    projectId: "project-1",
    conversationId: "conversation-1",
    contextId: "context-1",
    title: "Trip",
    executionCount: 1,
    messageCount: 2,
    createTime: "2026-10-23T08:00:00Z",
    usage: {
      inputTokenCount: 3,
      outputTokenCount: 4,
      totalTokenCount: 7,
      cachedInputTokenCount: 0,
      reasoningTokenCount: 0,
      unsettledTurnIds: [],
    },
    resumeState: null,
  };
}

function textMessage(messageId: string, extra: Partial<AiMessage> = {}): AiMessage {
  return {
    messageId,
    role: "assistant",
    author: "agent",
    contents: [{ type: "TextContent", content: messageId }],
    ...extra,
  };
}

function withoutIdentity(event: ChatEvent<AiMessage>): Record<string, unknown> {
  const { id, time, ...rest } = event;
  assert.match(id, /^[0-9a-f-]{36}$/u);
  assert.equal(new Date(time).toISOString(), time);
  return rest;
}

async function createHttpClient(
  routes: ApiRoutes,
  identity?: () => ChatConnectionIdentity,
): Promise<{ client: ChatHttpClient; server: ApiServer; events: ChatEvent<AiMessage>[] }> {
  const server = await startApiServer(routes);
  const client = new ChatHttpClient({
    api: createBearerApiClient({ baseUrl: server.baseUrl, token: "token-1" }),
    identity: () => identity?.() ?? { baseUrl: server.baseUrl, token: "token-1" },
    execution: () => ({ baseUrl: "https://agw.test", token: "hub-token", attachmentStore: null }),
  });
  const events: ChatEvent<AiMessage>[] = [];
  client.subscribe({}, (event) => events.push(event));
  return { client, server, events };
}

function createExecutionClient(execution?: () => ExecutionRuntimeConfig): ChatHttpClient {
  return new ChatHttpClient({
    api: createBearerApiClient({ baseUrl: "https://agw.test", token: "token-1" }),
    identity: () => ({ baseUrl: "https://agw.test", token: "token-1" }),
    execution:
      execution ??
      (() => ({ baseUrl: "https://agw.test", token: "hub-token", attachmentStore: null })),
  });
}

function hubCalls(events: ChatEvent<AiMessage>[]): string[] {
  return events.map((event) =>
    event.kind === "message"
      ? `message:${event.message.messageId}`
      : `${event.kind}:${event.request.method}`,
  );
}

test("getConversation dispatches its request and response and returns the details", async () => {
  const { client, events } = await createHttpClient({
    [`GET ${conversationPath}`]: conversationDetails(),
  });

  const details = await client.getConversation(scope);

  assert.equal(details.conversationId, "conversation-1");
  assert.equal(details.title, "Trip");
  assert.equal(events.length, 2);
  const [request, response] = events;
  assert.deepEqual(withoutIdentity(request!), {
    kind: "request",
    scope,
    origin: { kind: "client" },
    channel: "http",
    request: {
      method: "GET",
      path: CONVERSATION_DETAILS_PATH,
      params: { path: { projectId: "project-1", conversationId: "conversation-1" } },
      body: null,
    },
  });
  assert.deepEqual(withoutIdentity(response!), {
    kind: "response",
    requestId: request!.id,
    scope,
    origin: { kind: "client" },
    channel: "http",
    request: (request as Extract<ChatEvent, { kind: "request" }>).request,
    ok: true,
    value: conversationDetails(),
  });
});

test("getConversationMessages dispatches each page item as a history message after the response", async () => {
  const input = textMessage("input-1", { role: "user" });
  const result = textMessage("result-1", { additionalProperties: { type: "result" } });
  const { client, events } = await createHttpClient({
    [`GET ${conversationPath}/messages`]: {
      items: [textMessage("message-1"), textMessage("message-2")],
      nextCursor: null,
      hasMore: false,
      turns: [
        {
          turnId: "turn-1",
          status: "completed",
          input,
          results: [result],
          hasProcessMessages: true,
        },
      ],
    },
  });

  const page = await client.getConversationMessages(scope, { direction: "older", pageSize: 50 });

  assert.deepEqual(
    page.items.map((message) => message.messageId),
    ["message-1", "message-2"],
  );
  assert.deepEqual(
    events.map((event) => event.kind),
    ["request", "response", "message", "message"],
  );
  const request = events[0]!;
  assert.deepEqual(
    events.slice(2).map((event) => {
      assert.equal(event.kind, "message");
      if (event.kind !== "message") return null;
      return {
        source: event.source,
        channel: event.channel,
        requestId: event.requestId,
        messageId: event.message.messageId,
      };
    }),
    [
      { source: "history", channel: "http", requestId: request.id, messageId: "message-1" },
      { source: "history", channel: "http", requestId: request.id, messageId: "message-2" },
    ],
  );
});

test("getConversationHistory reads the details and every newer page, each with its own events", async () => {
  const { client, events } = await createHttpClient({
    [`GET ${conversationPath}`]: conversationDetails(),
    [`GET ${conversationPath}/messages`]: (request: ApiRequestRecord) =>
      request.query.get("cursor") === "cursor-1"
        ? { items: [textMessage("message-2")], nextCursor: null, hasMore: false, turns: [] }
        : { items: [textMessage("message-1")], nextCursor: "cursor-1", hasMore: true, turns: [] },
  });

  const history = await client.getConversationHistory(scope);

  assert.equal(history.title, "Trip");
  assert.deepEqual(
    history.messages.map((message) => message.messageId),
    ["message-1", "message-2"],
  );
  assert.deepEqual(hubCalls(events), [
    "request:GET",
    "response:GET",
    "request:GET",
    "response:GET",
    "message:message-1",
    "request:GET",
    "response:GET",
    "message:message-2",
  ]);
  const pages = events.filter((event) => event.kind === "request").slice(1);
  assert.deepEqual(
    pages.map(
      (event) =>
        event.kind === "request" && event.channel === "http" && event.request.params?.query,
    ),
    [
      { direction: "newer", pageSize: 100 },
      { direction: "newer", cursor: "cursor-1", pageSize: 100 },
    ],
  );
});

test("getConversationTurnInputs reports every page request through the given client", async () => {
  const { client, server, events } = await createHttpClient({
    "GET /api/projects/conversation-turns": (request: ApiRequestRecord) =>
      request.query.has("beforeTurnId")
        ? {
            items: [{ inputMessageId: "input-1", inputSummary: "first" }],
            nextBeforeSequence: null,
            nextBeforeTurnId: null,
            hasMore: false,
          }
        : {
            items: [{ inputMessageId: "input-2", inputSummary: "second" }],
            nextBeforeSequence: 4,
            nextBeforeTurnId: "turn-2",
            hasMore: true,
          },
  });

  const inputs = await client.getConversationTurnInputs(scope);

  assert.deepEqual(inputs, [
    { inputMessageId: "input-1", inputSummary: "first" },
    { inputMessageId: "input-2", inputSummary: "second" },
  ]);
  assert.deepEqual(
    events.map((event) => event.kind),
    ["request", "response", "request", "response"],
  );
  assert.deepEqual(
    server.requests.map((request) => request.headers.authorization),
    ["Bearer token-1", "Bearer token-1"],
  );
});

test("a failed request dispatches an unsuccessful response with the status and the ApiResult", async () => {
  const notFound = { code: 4040001, title: "Not Found" };
  const { client, events } = await createHttpClient({
    [`DELETE ${conversationPath}/clear-records`]: rawApiResponse({
      status: 404,
      headers: { "content-type": "application/json" },
      body: JSON.stringify(notFound),
    }),
  });

  const cleared = await client.clearConversationRecords(scope);

  assert.equal(cleared, false);
  const response = events[1]!;
  assert.equal(response.kind, "response");
  if (response.kind !== "response" || response.ok) return;
  assert.equal(response.request.method, "DELETE");
  assert.equal(response.error.name, "ApiError");
  assert.equal(response.error.status, 404);
  assert.deepEqual(response.error.body, notFound);
});

test("a transport failure dispatches a response without a status or body and rejects", async () => {
  const { client, server, events } = await createHttpClient({});
  await server.close();

  await assert.rejects(client.getConversation(scope), ApiTransportError);

  const response = events[1]!;
  assert.equal(response.kind, "response");
  if (response.kind !== "response" || response.ok) return;
  assert.equal(response.error.name, "ApiTransportError");
  assert.equal(response.error.status, null);
  assert.equal(response.error.body, null);
});

test("subscribers receive frozen events while callers keep mutable results", async () => {
  const { client, events } = await createHttpClient({
    [`GET ${conversationPath}/messages`]: {
      items: [textMessage("message-1")],
      nextCursor: null,
      hasMore: false,
      turns: [],
    },
  });

  const page = await client.getConversationMessages(scope, { direction: "older" });
  const message = events[2]!;

  assert.equal(message.kind, "message");
  if (message.kind !== "message") return;
  assert.throws(() => {
    message.message.messageId = "changed";
  }, TypeError);
  page.items[0]!.messageId = "changed";
  assert.equal(message.message.messageId, "message-1");
});

test("subscriptions follow the dispatch-time list and stop after unsubscribing", async () => {
  const { client } = await createHttpClient({ [`GET ${conversationPath}`]: conversationDetails() });
  const received: string[] = [];
  let unsubscribeSecond = () => {};
  const unsubscribeFirst = client.subscribe({ kinds: ["request"] }, () => {
    received.push("first");
    unsubscribeSecond();
    client.subscribe({ kinds: ["request"] }, () => received.push("added"));
  });
  unsubscribeSecond = client.subscribe({ kinds: ["request"] }, () => received.push("second"));

  await client.getConversation(scope);
  unsubscribeFirst();
  await client.getConversation(scope);

  assert.deepEqual(received, ["first", "added"]);
});

test("a request subscriber that throws keeps the request from being sent", async () => {
  const { client, server } = await createHttpClient({
    [`GET ${conversationPath}`]: conversationDetails(),
  });
  client.subscribe({ kinds: ["request"] }, () => {
    throw new Error("listener failed");
  });

  await assert.rejects(client.getConversation(scope), /listener failed/);

  assert.equal(server.requests.length, 0);
});

test("HTTP events carry the connection identity of the moment the request was sent", async () => {
  let identity: ChatConnectionIdentity = { baseUrl: "", token: "token-1" };
  const { client, server } = await createHttpClient(
    {
      [`GET ${conversationPath}/messages`]: () => {
        identity = { baseUrl: "https://other.test/", token: "token-2" };
        return { items: [textMessage("message-1")], nextCursor: null, hasMore: false, turns: [] };
      },
    },
    () => identity,
  );
  identity = { baseUrl: ` ${server.baseUrl}/ `, token: "token-1" };
  const connections: ChatConnectionIdentity[] = [];
  client.subscribe({}, (_event, connection) => connections.push(connection));

  await client.getConversationMessages(scope, { direction: "older" });

  assert.deepEqual(connections, Array(3).fill({ baseUrl: server.baseUrl, token: "token-1" }));
  assert.deepEqual(client.getConnectionIdentity(), {
    baseUrl: "https://other.test",
    token: "token-2",
  });
});

test("HTTP calls made for an extension carry its origin on request, response and history events", async () => {
  const origin = { kind: "extension" as const, extensionId: "sample" };
  const { client, events } = await createHttpClient({
    [`GET ${conversationPath}`]: conversationDetails(),
    [`GET ${conversationPath}/messages`]: {
      items: [textMessage("message-1")],
      nextCursor: null,
      hasMore: false,
      turns: [],
    },
    [`DELETE ${conversationPath}/clear-records`]: null,
    "GET /api/projects/conversation-turns": {
      items: [],
      nextBeforeSequence: null,
      nextBeforeTurnId: null,
      hasMore: false,
    },
    "GET /api/agents/permission-capabilities": { supportedPermissionModes: [], reason: null },
    "GET /api/agents/suggestions": { suggestions: [] },
  });

  await client.getConversation(scope, undefined, origin);
  await client.getConversationMessages(scope, { direction: "newer" }, origin);
  await client.getConversationHistory(scope, undefined, origin);
  await client.getConversationTurnInputs(scope, undefined, origin);
  await client.clearConversationRecords(scope, origin);
  await client.getPermissionCapabilities(
    scope,
    { type: "agent", id: "agent-1" },
    undefined,
    origin,
  );
  await client.getAgentSuggestions(scope, { agentId: "agent-1" }, undefined, origin);

  assert.deepEqual([...new Set(events.map((event) => event.kind))].sort(), [
    "message",
    "request",
    "response",
  ]);
  assert.ok(events.every((event) => JSON.stringify(event.origin) === JSON.stringify(origin)));
});

test("an extension session sends its extension id in every SettingCommand, also after a reconnect", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const client = createExecutionClient();
  const settings: unknown[] = [];
  client.subscribe({ kinds: ["request"], channels: ["execution"] }, (event) => {
    if (event.kind !== "request" || event.channel !== "execution") return;
    const command = event.request.args[0] as { type?: string } | undefined;
    if (command?.type === "SettingCommand") settings.push(command);
  });
  const session = client.createExecutionSession(
    scope,
    { onMessage() {} },
    { origin: { kind: "extension", extensionId: "sample" } },
  );
  try {
    await session.configure({ ...setting, permissionMode: "alwaysAsk" });
    hubs[0]!.reconnect();
    for (let attempt = 0; attempt < 50 && settings.length < 2; attempt++) {
      await new Promise((resolve) => setImmediate(resolve));
    }

    assert.equal(settings.length, 2);
    for (const command of settings) {
      assert.deepEqual(command, {
        type: "SettingCommand",
        projectId: "project-1",
        conversationId: "conversation-1",
        permissionMode: "alwaysAsk",
        extensionId: "sample",
      });
    }
  } finally {
    await session.dispose();
  }
});

test("a host session never sends an extension id", async (t) => {
  replaceExecutionHub(t, { provider: "InProcess" });
  const client = createExecutionClient();
  const settings: Record<string, unknown>[] = [];
  client.subscribe({ kinds: ["request"], channels: ["execution"] }, (event) => {
    if (event.kind !== "request" || event.channel !== "execution") return;
    const command = event.request.args[0] as Record<string, unknown> | undefined;
    if (command?.type === "SettingCommand") settings.push(command);
  });
  const session = client.createExecutionSession(scope, { onMessage() {} });
  try {
    await session.configure({ ...setting, extensionId: "forged" } as typeof setting);

    assert.equal(settings.length, 1);
    assert.equal("extensionId" in settings[0]!, false);
  } finally {
    await session.dispose();
  }
});

test("subscribe rejects an invalid filter", () => {
  const client = createExecutionClient();

  assert.throws(() => client.subscribe({ kinds: [] } as ChatEventFilter, () => {}), /kinds/);
});

test("an execution session reports its hub calls as execution events of its scope", async (t) => {
  replaceExecutionHub(t, { provider: "InProcess" });
  const client = createExecutionClient();
  const events: ChatEvent<AiMessage>[] = [];
  client.subscribe({ channels: ["execution"] }, (event) => events.push(event));
  const session = client.createExecutionSession(scope, { onMessage() {} });
  try {
    await session.configure(setting);

    assert.deepEqual(hubCalls(events), [
      "request:GetExecutionProvider",
      "response:GetExecutionProvider",
      "request:DispatchCommand",
      "response:DispatchCommand",
      "request:FindInProcessExecution",
      "response:FindInProcessExecution",
    ]);
    assert.ok(events.every((event) => JSON.stringify(event.scope) === JSON.stringify(scope)));
    assert.ok(events.every((event) => event.origin.kind === "client"));
    const dispatch = events[2]!;
    assert.ok(dispatch.kind === "request" && dispatch.channel === "execution");
    assert.equal((dispatch.request.args[0] as { type: string }).type, "SettingCommand");
    const dispatched = events[3]!;
    assert.ok(dispatched.kind === "response" && dispatched.ok);
    assert.equal(dispatched.value, null);
    assert.equal(dispatched.requestId, dispatch.id);
  } finally {
    await session.dispose();
  }
});

test("a failed hub call dispatches an unsuccessful response and the caller gets the error", async (t) => {
  const failure = new Error("hub failed");
  replaceExecutionHub(t, {
    provider: "InProcess",
    onInvoke: ({ method }) => {
      if (method === "GetAgentflowCheckpoints") throw failure;
    },
  });
  const client = createExecutionClient();
  const events: ChatEvent<AiMessage>[] = [];
  client.subscribe({ kinds: ["response"], channels: ["execution"] }, (event) => events.push(event));
  const session = client.createExecutionSession(scope, { onMessage() {} });
  try {
    await session.configure(setting);

    await assert.rejects(session.listAgentflowCheckpoints("flow-1"), (error) => error === failure);

    const response = events.at(-1)!;
    assert.ok(response.kind === "response" && response.channel === "execution" && !response.ok);
    assert.equal(response.request.method, "GetAgentflowCheckpoints");
    assert.deepEqual(response.error, {
      name: "Error",
      message: "hub failed",
      status: null,
      body: null,
    });
  } finally {
    await session.dispose();
  }
});

test("a pushed message reaches the handler first and is dispatched as the server sent it", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const client = createExecutionClient();
  const order: string[] = [];
  client.subscribe({ kinds: ["message"] }, (event) => {
    if (event.kind !== "message") return;
    order.push(
      `event:${event.source}:${event.channel}:${event.requestId}:${"streamingScopeId" in event.message}`,
    );
  });
  const session = client.createExecutionSession(scope, {
    onMessage: (message) => order.push(`handler:${message.streamingScopeId}`),
  });
  await session.configure(setting);

  hubs[0]!.receive(textMessage("text-1"));
  await session.dispose();
  hubs[0]!.receive(textMessage("text-2"));

  assert.deepEqual(order, ["handler:text-1", "event:live:execution:null:false"]);
});

test("pushed and history messages can be subscribed to separately", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const server = await startApiServer({
    [`GET ${conversationPath}/messages`]: {
      items: [textMessage("history-1")],
      nextCursor: null,
      hasMore: false,
      turns: [],
    },
  });
  const client = new ChatHttpClient({
    api: createBearerApiClient({ baseUrl: server.baseUrl, token: "token-1" }),
    identity: () => ({ baseUrl: server.baseUrl, token: "token-1" }),
    execution: () => ({ baseUrl: "https://agw.test", token: "hub-token", attachmentStore: null }),
  });
  const received = { live: [] as string[], history: [] as string[], all: [] as string[] };
  const record = (bucket: string[]) => (event: ChatEvent<AiMessage>) => {
    if (event.kind === "message") bucket.push(event.message.messageId);
  };
  client.subscribe({ sources: ["live"] }, record(received.live));
  client.subscribe({ sources: ["history"] }, record(received.history));
  client.subscribe({ kinds: ["message"] }, record(received.all));
  const session = client.createExecutionSession(scope, { onMessage() {} });
  try {
    await session.configure(setting);
    await client.getConversationMessages(scope, { direction: "older" });

    hubs[0]!.receive(textMessage("live-1"));

    assert.deepEqual(received, {
      live: ["live-1"],
      history: ["history-1"],
      all: ["history-1", "live-1"],
    });
  } finally {
    await session.dispose();
  }
});

test("an extension session carries its origin and connects with the given runtime", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  class ExtensionWebSocket {}
  const client = createExecutionClient();
  const events: ChatEvent<AiMessage>[] = [];
  client.subscribe({ channels: ["execution"] }, (event) => events.push(event));
  const session = client.createExecutionSession(
    scope,
    { onMessage() {} },
    {
      runtime: {
        baseUrl: "https://extension.test",
        token: "extension-token",
        webSocket: ExtensionWebSocket as unknown as typeof WebSocket,
        attachmentStore: null,
      },
      origin: { kind: "extension", extensionId: "sample" },
    },
  );
  try {
    await session.configure(setting);

    assert.ok(events.length > 0);
    assert.ok(
      events.every(
        (event) => JSON.stringify(event.origin) === '{"kind":"extension","extensionId":"sample"}',
      ),
    );
    assert.equal(hubs[0]!.url, "https://extension.test/api/hubs/exec");
    assert.equal(hubs[0]!.connectionOptions.WebSocket, ExtensionWebSocket);
  } finally {
    await session.dispose();
  }
});

test("execution events keep the connection identity the session was created with", async (t) => {
  replaceExecutionHub(t, { provider: "InProcess" });
  let runtime: ExecutionRuntimeConfig = {
    baseUrl: " https://agw.test/ ",
    token: "hub-token",
    attachmentStore: null,
  };
  const client = createExecutionClient(() => runtime);
  const connections: ChatConnectionIdentity[] = [];
  client.subscribe({ channels: ["execution"] }, (_event, connection) =>
    connections.push(connection),
  );
  const session = client.createExecutionSession(scope, { onMessage() {} });
  runtime = { baseUrl: "https://other.test", token: "other-token", attachmentStore: null };
  try {
    await session.configure(setting);

    assert.ok(connections.length > 0);
    assert.deepEqual(
      connections,
      Array(connections.length).fill({ baseUrl: "https://agw.test", token: "hub-token" }),
    );
  } finally {
    await session.dispose();
  }
});

test("a reconnect reports the restored settings and durable subscription", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const client = createExecutionClient();
  const commands: string[] = [];
  const session = client.createExecutionSession(scope, { onMessage() {} });
  try {
    await session.configure(setting);
    await session.execute({
      conversationId: "conversation-1",
      agentId: "agent-1",
      agentType: 0,
      turnId: "turn-1",
      input: { messageId: "input-1", author: "$agw", contents: [] },
    });
    client.subscribe({ kinds: ["request"], channels: ["execution"] }, (event) => {
      if (
        event.kind === "request" &&
        event.channel === "execution" &&
        event.request.method === "DispatchCommand"
      ) {
        commands.push((event.request.args[0] as { type: string }).type);
      }
    });

    hubs[0]!.reconnect();
    for (let attempt = 0; attempt < 50 && commands.length < 2; attempt++) {
      await new Promise((resolve) => setImmediate(resolve));
    }

    assert.deepEqual(commands, ["SettingCommand", "SubscribeTurnCommand"]);
  } finally {
    await session.dispose();
  }
});

test("a pushed message whose subscriber throws leaves the durable cursor unconfirmed", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const values = new Map<string, string>();
  const store: ExecutionAttachmentStore = {
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => void values.set(key, value),
    removeItem: (key) => void values.delete(key),
  };
  const runtime = { baseUrl: "https://agw.test", token: "hub-token", attachmentStore: store };
  const client = createExecutionClient(() => runtime);
  client.subscribe({ sources: ["live"] }, (event) => {
    if (event.kind === "message" && event.message.additionalProperties?.turnSequence === 2) {
      throw new Error("subscriber failed");
    }
  });
  const session = client.createExecutionSession(scope, { onMessage() {} });
  const position = (turnSequence: number) =>
    textMessage(`part-${turnSequence}`, {
      additionalProperties: { turnId: "turn-1", turnSequence },
    });
  try {
    await session.configure(setting);
    await session.execute({
      conversationId: "conversation-1",
      agentId: "agent-1",
      agentType: 0,
      turnId: "turn-1",
      input: { messageId: "input-1", author: "$agw", contents: [] },
    });
    hubs[0]!.receive(position(1));

    assert.throws(() => hubs[0]!.receive(position(2)), /subscriber failed/);

    const attachment = JSON.parse(values.get(getDurableExecutionStorageKey(runtime, setting))!);
    assert.deepEqual(attachment, { turnId: "turn-1", cursor: "1" });
  } finally {
    await session.dispose();
  }
});
