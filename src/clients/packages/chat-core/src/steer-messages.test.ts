import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { mergeStreamingMessages, scopeStreamingMessage } from "@agw/execution-core";
import { buildConversationRenderModel } from "./conversation-render-model";
import { prepareConversationHistory } from "./message-presentation";

const turnId = "turn-1";

function message(
  messageId: string,
  role: string,
  content: string,
  additionalProperties: Record<string, unknown> = {},
): AiMessage {
  return {
    messageId,
    role,
    author: role === "user" ? undefined : "agent",
    contents: [{ type: "TextContent", content }],
    additionalProperties: { turnId, ...additionalProperties },
  };
}

/** 按到达顺序把消息合并进同一个 scope，与 ChatWorkspace 的实时合并一致。Merges messages into one scope in arrival order, as the live merge does. */
function mergeLive(messages: readonly AiMessage[]): AiMessage[] {
  return messages.reduce<AiMessage[]>(
    (merged, incoming) => mergeStreamingMessages(merged, [scopeStreamingMessage(incoming, turnId)]),
    [],
  );
}

const steerUser = message("steer-1", "user", "use the other file", {
  conversationId: "conversation-1",
  streamingScopeId: turnId,
  steerId: "steer-1",
});
const steerDelivered = message("control-delivered", "system", "", {
  type: "steer-delivered",
  conversationId: "conversation-1",
  streamingScopeId: turnId,
  steerId: "steer-1",
  stepIndex: 1,
});
const steerReturned = message("control-returned", "system", "never delivered", {
  type: "steer-returned",
  conversationId: "conversation-1",
  streamingScopeId: turnId,
  steerId: "steer-2",
  reason: "turn-completed",
});

test("a delivered Steer user message appears in the middle of its turn, where the model saw it", () => {
  const live = mergeLive([
    message("input-1", "user", "refactor the module"),
    message("assistant-1", "assistant", "reading the files"),
    steerUser,
    steerDelivered,
    message("assistant-2", "assistant", "switching to the other file"),
  ]);

  const items = buildConversationRenderModel(live);

  assert.deepEqual(
    items.map((item) => (item.type === "message" ? item.message.source.messageId : item.type)),
    ["input-1", "assistant-1", "steer-1", "assistant-2"],
  );
  const steerItem = items[2];
  assert.equal(steerItem?.type, "message");
  assert.equal(steerItem?.alignment, "right");
});

test("a replayed Steer user message merges by messageId and scope instead of duplicating", () => {
  const live = mergeLive([
    message("input-1", "user", "refactor the module"),
    steerUser,
    steerUser,
    message("assistant-1", "assistant", "done"),
  ]);

  assert.equal(live.filter((item) => item.messageId === "steer-1").length, 1);
  assert.equal(
    live.find((item) => item.messageId === "steer-1")?.contents[0]?.content,
    "use the other file",
  );
});

test("steer-delivered and steer-returned never render as chat bubbles", () => {
  const live = mergeLive([
    message("input-1", "user", "refactor the module"),
    steerDelivered,
    steerReturned,
    message("assistant-1", "assistant", "done"),
  ]);

  const items = buildConversationRenderModel(live);

  assert.deepEqual(
    items.map((item) => (item.type === "message" ? item.message.source.messageId : item.type)),
    ["input-1", "assistant-1"],
  );
});

test("history preparation drops the Steer control messages and keeps the Steer user message", () => {
  const prepared = prepareConversationHistory([
    message("input-1", "user", "refactor the module"),
    steerUser,
    steerDelivered,
    steerReturned,
    message("assistant-1", "assistant", "done"),
  ]);

  assert.deepEqual(
    prepared.messages.map((item) => item.messageId),
    ["input-1", "steer-1", "assistant-1"],
  );
});
