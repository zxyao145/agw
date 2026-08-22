import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { buildConversationRenderModel, getMessageMeta } from "@agw/chat-core";

const CONVERSATION_URL = new URL("./conversation.tsx", import.meta.url);
const PRESENTED_MESSAGE_URL = new URL("./presented-message.tsx", import.meta.url);

test("conversation is a thin host over the shared render model union", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");
  assert.match(source, /type \{ ConversationRenderItem \} from "@agw\/chat-core"/);
  assert.match(source, /items: ConversationRenderItem\[\]/);
  assert.doesNotMatch(source, /processMessages|collapseConsecutiveSystemMessages|callId/);
  assert.match(source, /item\.type === "tool-accordion"/);
  assert.match(source, /item\.type === "human-interaction"/);
  assert.match(source, /item\.type === "checkpoint"/);
});

test("agent metadata keeps name priority and independent author", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      author: "model-author",
      contents: [],
      additionalProperties: {
        nodeName: "Review Node",
        name: "Fallback",
        agentName: "general-agent",
      },
    }),
    { name: "Review Node", author: "model-author" },
  );
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-2",
      role: "assistant",
      contents: [],
      additionalProperties: { agentName: "general-agent" },
    }),
    { name: "general-agent", author: null },
  );
});

test("shared model provides stable per-turn tool groups to the renderer", () => {
  const tool = (type: string, scope: string): AiMessage => ({
    messageId: `${type}-${scope}`,
    role: type === "FunctionCallContent" ? "assistant" : "tool",
    author: "agent",
    streamingScopeId: scope,
    contents: [
      {
        type,
        content: "{}",
        additionalProperties: { callId: "call-1", toolName: "command_execution" },
      },
    ],
  });
  const items = buildConversationRenderModel([
    tool("FunctionCallContent", "user-1"),
    tool("FunctionResultContent", "user-1"),
    tool("FunctionCallContent", "user-2"),
    tool("FunctionResultContent", "user-2"),
  ]);
  assert.deepEqual(
    items.map((item) => item.type),
    ["tool-accordion", "tool-accordion"],
  );
  assert.notEqual(items[0].key, items[1].key);
});

test("DOM renderer applies the requested width and alignment rules", async () => {
  const [conversation, message] = await Promise.all([
    readFile(CONVERSATION_URL, "utf8"),
    readFile(PRESENTED_MESSAGE_URL, "utf8"),
  ]);
  assert.match(conversation, /max-w-\[80%\]/);
  assert.match(message, /bg-\[#f3f3f4\]/);
  assert.match(message, /message\.width === "full"/);
  assert.match(message, /text-destructive/);
});
