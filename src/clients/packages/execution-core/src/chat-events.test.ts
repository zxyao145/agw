import assert from "node:assert/strict";
import test from "node:test";
import {
  matchesChatEvent,
  snapshotChatEvent,
  validateChatEventFilter,
  type ChatEvent,
  type ChatEventFilter,
  type ChatMessageEvent,
  type ChatRequestEvent,
  type ChatResponseEvent,
} from "./chat-events";
import type { ExecutionMessage } from "./types";

const scope = { serverId: "browser", projectId: "project-1", conversationId: "conversation-1" };

function requestEvent(overrides: Partial<ChatRequestEvent> = {}): ChatRequestEvent {
  return {
    id: "request-1",
    time: "2026-10-23T08:00:00.000Z",
    scope,
    origin: { kind: "client" },
    kind: "request",
    channel: "http",
    request: {
      method: "GET",
      path: "/api/projects/{projectId}/conversations/{conversationId}",
      params: { path: { projectId: "project-1", conversationId: "conversation-1" } },
      body: null,
    },
    ...overrides,
  } as ChatRequestEvent;
}

function responseEvent(): ChatResponseEvent {
  return {
    id: "response-1",
    time: "2026-10-23T08:00:01.000Z",
    scope,
    origin: { kind: "extension", extensionId: "sample" },
    kind: "response",
    requestId: "request-2",
    channel: "execution",
    request: { method: "DispatchCommand", args: [{ type: "ExecCommand" }] },
    ok: true,
    value: null,
  };
}

function messageEvent(
  message: Partial<ExecutionMessage>,
  source: "live" | "history" = "live",
): ChatMessageEvent {
  const part =
    source === "live"
      ? ({ source: "live", channel: "execution", requestId: null } as const)
      : ({ source: "history", channel: "http", requestId: "page-1" } as const);
  return {
    id: `message-${message.messageId ?? "1"}`,
    time: "2026-10-23T08:00:02.000Z",
    scope,
    origin: { kind: "client" },
    kind: "message",
    message: { messageId: "1", contents: [], ...message },
    ...part,
  };
}

const request = requestEvent();
const response = responseEvent();
const assistant = messageEvent({
  role: "assistant",
  contents: [{ type: "TextContent", content: "hello" }],
});

function matching(filter: ChatEventFilter, events: readonly ChatEvent[]): string[] {
  return events.filter((event) => matchesChatEvent(filter, event)).map((event) => event.id);
}

test("an empty filter matches requests, responses and messages", () => {
  assert.deepEqual(matching({}, [request, response, assistant]), [
    "request-1",
    "response-1",
    "message-1",
  ]);
});

test("each event field lets only the matching events through", () => {
  const otherScope = requestEvent({
    id: "request-other",
    scope: { serverId: "desktop", projectId: "project-2", conversationId: "conversation-2" },
  });
  const events = [request, response, assistant, otherScope];

  assert.deepEqual(matching({ kinds: ["response"] }, events), ["response-1"]);
  assert.deepEqual(matching({ channels: ["execution"] }, events), ["response-1", "message-1"]);
  assert.deepEqual(matching({ origins: ["extension"] }, events), ["response-1"]);
  assert.deepEqual(matching({ serverId: "desktop" }, events), ["request-other"]);
  assert.deepEqual(matching({ projectId: "project-2" }, events), ["request-other"]);
  assert.deepEqual(matching({ conversationId: "conversation-1" }, events), [
    "request-1",
    "response-1",
    "message-1",
  ]);
  assert.deepEqual(
    matching({ kinds: ["request", "response"], channels: ["http"], serverId: "browser" }, events),
    ["request-1"],
  );
});

test("a scope field that is null never satisfies the same filter field", () => {
  const outsideConversation = requestEvent({
    id: "request-outside",
    scope: { serverId: "browser", projectId: null, conversationId: null },
  });

  assert.equal(matchesChatEvent({ projectId: "project-1" }, outsideConversation), false);
  assert.equal(matchesChatEvent({ conversationId: "conversation-1" }, outsideConversation), false);
  assert.equal(matchesChatEvent({ serverId: "browser" }, outsideConversation), true);
});

test("roles ignore case and skip messages without a role", () => {
  const upperCase = messageEvent({ messageId: "upper", role: "Assistant" });
  const withoutRole = messageEvent({ messageId: "none" });
  const user = messageEvent({ messageId: "user", role: "user" });

  assert.deepEqual(matching({ roles: ["ASSISTANT"] }, [assistant, upperCase, withoutRole, user]), [
    "message-1",
    "message-upper",
  ]);
});

test("message types match the type field, and null matches messages without one", () => {
  const result = messageEvent({ messageId: "result", additionalProperties: { type: "result" } });
  const finished = messageEvent({
    messageId: "finished",
    additionalProperties: { type: "agw-turn-finished", turnId: "turn-1" },
  });
  const numericType = messageEvent({ messageId: "numeric", additionalProperties: { type: 3 } });
  const events = [result, finished, numericType, assistant];

  assert.deepEqual(matching({ messageTypes: ["result"] }, events), ["message-result"]);
  assert.deepEqual(matching({ messageTypes: [null] }, events), ["message-numeric", "message-1"]);
  assert.deepEqual(matching({ messageTypes: ["agw-turn-finished", null] }, events), [
    "message-finished",
    "message-numeric",
    "message-1",
  ]);
});

test("content types match a message holding any listed content", () => {
  const usage = messageEvent({
    messageId: "usage",
    contents: [
      { type: "TextContent", content: "" },
      { type: "UsageContent", content: { totalTokenCount: 2 } },
    ],
  });
  const call = messageEvent({ messageId: "call", contents: [{ type: "FunctionCallContent" }] });

  assert.deepEqual(matching({ contentTypes: ["UsageContent"] }, [usage, call, assistant]), [
    "message-usage",
  ]);
  assert.deepEqual(
    matching({ contentTypes: ["FunctionCallContent", "FunctionResultContent"] }, [usage, call]),
    ["message-call"],
  );
});

test("sources separate pushed messages from history messages", () => {
  const history = messageEvent({ messageId: "history", role: "assistant" }, "history");
  const events = [assistant, history];

  assert.deepEqual(matching({ sources: ["live"] }, events), ["message-1"]);
  assert.deepEqual(matching({ sources: ["history"] }, events), ["message-history"]);
  assert.deepEqual(matching({ sources: ["live", "history"] }, events), [
    "message-1",
    "message-history",
  ]);
});

test("message fields exclude requests and responses", () => {
  for (const filter of [
    { sources: ["live", "history"] },
    { roles: ["assistant"] },
    { messageTypes: [null] },
    { contentTypes: ["TextContent"] },
  ] satisfies ChatEventFilter[]) {
    assert.deepEqual(matching(filter, [request, response, assistant]), ["message-1"]);
  }
});

test("a valid filter has no problem", () => {
  assert.equal(validateChatEventFilter({}), null);
  assert.equal(
    validateChatEventFilter({
      kinds: ["request", "response", "message"],
      channels: ["http", "execution"],
      origins: ["client", "extension"],
      serverId: "browser",
      projectId: "project-1",
      conversationId: "conversation-1",
      sources: ["live", "history"],
      roles: ["assistant"],
      messageTypes: ["result", null],
      contentTypes: ["UsageContent"],
    }),
    null,
  );
  assert.equal(validateChatEventFilter({ kinds: undefined }), null);
});

test("an invalid filter reports the problem", () => {
  const cases: [unknown, RegExp][] = [
    [null, /object/],
    [["kinds"], /object/],
    ["kinds", /object/],
    [new Date(), /object/],
    [{ kind: ["request"] }, /kind/],
    [{ kinds: [] }, /kinds/],
    [{ kinds: ["event"] }, /kinds/],
    [{ kinds: "request" }, /kinds/],
    [{ channels: ["websocket"] }, /channels/],
    [{ origins: ["server"] }, /origins/],
    [{ sources: ["replay"] }, /sources/],
    [{ serverId: "" }, /serverId/],
    [{ projectId: 1 }, /projectId/],
    [{ conversationId: null }, /conversationId/],
    [{ roles: [""] }, /roles/],
    [{ roles: [] }, /roles/],
    [{ contentTypes: [3] }, /contentTypes/],
    [{ messageTypes: [""] }, /messageTypes/],
    [{ messageTypes: [1] }, /messageTypes/],
    [{ messageTypes: [undefined] }, /messageTypes/],
  ];
  for (const [value, problem] of cases) {
    const description = validateChatEventFilter(value);
    assert.notEqual(description, null, `expected a problem for ${JSON.stringify(value)}`);
    assert.match(description!, problem);
  }
});

test("a snapshot is a frozen deep copy without undefined fields", () => {
  const original = messageEvent({
    role: "assistant",
    contents: [
      { type: "UsageContent", content: { totalTokenCount: 2 }, additionalProperties: undefined },
    ],
    additionalProperties: { turnId: "turn-1", turnSequence: 3 },
  });

  const snapshot = snapshotChatEvent(original);

  assert.deepEqual(snapshot, JSON.parse(JSON.stringify(original)));
  assert.equal(Object.isFrozen(snapshot), true);
  assert.equal(Object.isFrozen(snapshot.scope), true);
  assert.equal(snapshot.kind, "message");
  if (snapshot.kind !== "message") return;
  assert.equal(Object.isFrozen(snapshot.message.contents), true);
  assert.equal(Object.isFrozen(snapshot.message.contents[0]), true);
  assert.equal(Object.isFrozen(snapshot.message.contents[0]!.content), true);
  assert.equal("additionalProperties" in snapshot.message.contents[0]!, false);
  assert.throws(() => {
    (snapshot.message.contents as unknown[]).push({ type: "TextContent" });
  }, TypeError);

  original.message.contents[0]!.type = "TextContent";
  original.scope = { ...scope, conversationId: "changed" };
  assert.equal(snapshot.message.contents[0]!.type, "UsageContent");
  assert.equal(snapshot.scope.conversationId, "conversation-1");
});
