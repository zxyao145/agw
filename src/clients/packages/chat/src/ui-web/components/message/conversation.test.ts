import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { getMessageMeta } from "@agw/chat-core";
import { processMessages } from "@agw/execution-core";

const CONVERSATION_URL = new URL("./conversation.tsx", import.meta.url);

function toolMessage(type: string, scope: string, callId = "item_1"): AiMessage {
  return toolContentsMessage(type, scope, [callId]);
}

function toolContentsMessage(type: string, scope: string, callIds: string[]): AiMessage {
  return {
    messageId: `${type}-${scope}`,
    author: "agent",
    role: type === "FunctionCallContent" ? "assistant" : "tool",
    streamingScopeId: scope,
    contents: callIds.map((callId) => ({
      type,
      content: `${type}-${callId}`,
      additionalProperties: {
        callId,
        ...(type === "FunctionCallContent" ? { toolName: `tool-${callId}` } : {}),
      },
    })),
  };
}

test("conversation renders agent name and author metadata above agent messages", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /import \{ getMessageMeta \} from "@agw\/chat-core"/);
  assert.match(source, /\{messageMeta\.name\}/);
  assert.match(source, /\{messageMeta\.author\}/);
  assert.match(source, /AiMessageComponent message=\{item\.message\}/);
});

test("conversation restores the agentflow node and agent names from persisted metadata", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      contents: [],
      additionalProperties: {
        nodeName: "Review Node",
        agentName: "general-agent",
        name: "Fallback Name",
      },
    }),
    { name: "Review Node", author: "general-agent" },
  );
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-2",
      author: "general-agent",
      role: "assistant",
      contents: [],
    }),
    { name: null, author: "general-agent" },
  );
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-3",
      role: "assistant",
      contents: [],
      additionalProperties: {
        agentName: "general-agent",
      },
    }),
    { name: null, author: "general-agent" },
  );
});

test("conversation delegates grouping to execution-core and keeps stable keys", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(
    source,
    /import \{ processMessages, type ProcessedMessageItem \} from "@agw\/execution-core"/,
  );
  assert.match(source, /const defaultProcessMessages = processMessages;/);
  assert.match(source, /function addStableKeys/);
  assert.match(source, /React\.useMemo\([\s\S]*?processMessages/);
  assert.doesNotMatch(source, /key=\{index\}|console\.(?:debug|log)/);
});

test("conversation reuses preprocessing for unchanged messages only", () => {
  const stableMessage: AiMessage = {
    messageId: "stable-message",
    author: "agent",
    role: "assistant",
    streamingScopeId: "user-1",
    contents: [{ type: "TextContent", content: "stable" }],
  };
  const activeMessage: AiMessage = {
    messageId: "active-message",
    author: "agent",
    role: "assistant",
    streamingScopeId: "user-1",
    contents: [{ type: "TextContent", content: "first" }],
  };
  const nextActiveMessage = {
    ...activeMessage,
    contents: [{ type: "TextContent", content: "second" }],
  };

  const first = processMessages([stableMessage, activeMessage]);
  const second = processMessages([stableMessage, nextActiveMessage]);

  assert.equal(first[0].type, "normal");
  assert.equal(second[0].type, "normal");
  assert.equal(first[0].message, second[0].message);
  assert.notEqual(first[1].message, second[1].message);
});

test("conversation renders user author metadata above and aligned with user messages", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.deepEqual(
    getMessageMeta({
      messageId: "user-1",
      author: "$agw",
      role: "user",
      contents: [],
    }),
    { name: null, author: "$agw" },
  );
  assert.match(source, /const isUserMessage = item\.message\.role === "user";/);
  assert.match(source, /isUserMessage \? "ml-auto justify-end" : ""/);
});

test("conversation can delegate scrolling while keeping messages centered", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /scrollable\?: boolean;/);
  assert.match(source, /scrollable = true,/);
  assert.match(source, /scrollable && "overflow-y-auto agw-scrollbar"/);
  assert.match(source, /<div className="mx-auto w-full max-w-225 space-y-4 pb-36">/);
});

test("conversation renders footer content before its bottom scroll anchor", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /\{footer\}[\s\S]*?<div ref=\{messagesEndRef\}/);
  assert.match(source, /messages\.length == 0\) && !footer/);
});

test("conversation renders checkpoint cards bound to their own occurrence", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /getAgentflowCheckpointMessage\(item\.message\)/);
  assert.match(source, /checkpointsByOccurrence\.get\(checkpoint\.occurrenceId\)/);
  assert.match(source, /onCheckpointResume\?\.\(checkpoint\.occurrenceId\)/);
  assert.match(source, /showResume=\{showCheckpointResume\}/);
});

test("conversation embeds a pending human interaction in its matching function call", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /matchesHumanInteractionCall\(item\.message, pendingHumanInteraction\)/);
  assert.match(source, /data-function-call-id=\{pendingHumanInteraction\.callId\}/);
  assert.match(source, /<HumanInteractionPanel[\s\S]*?embedded/);
});

test("conversation renders completed ask_user_question calls as question and answer text", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /item\.toolName === "ask_user_question"/);
  assert.match(source, /getHumanInteractionQuestionResult\(item\.messages\)/);
  assert.match(source, /<HumanInteractionQuestionResultView result=\{questionResult\}/);
});

test("conversation delegates filtering and grouping to execution-core", async () => {
  const source = await readFile(CONVERSATION_URL, "utf8");

  assert.match(source, /collapseConsecutiveSystemMessages\(messages\)/);
  assert.doesNotMatch(source, /if \(message\.role === "system"\) \{\s*continue;/);
  assert.doesNotMatch(source, /message\.role === "user" && !message\.author/);
});

test("conversation restores persisted authorless assistant and tool messages", () => {
  const items = processMessages([
    {
      messageId: "assistant-1",
      author: null,
      role: "assistant",
      streamingScopeId: "user-1",
      contents: [
        { type: "TextReasoningContent", content: "Planning the task" },
        {
          type: "FunctionCallContent",
          content: "{}",
          additionalProperties: { callId: "call-1", toolName: "todos_add" },
        },
      ],
    },
    {
      messageId: "",
      author: null,
      role: "tool",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionResultContent",
          content: "[]",
          additionalProperties: { callId: "call-1" },
        },
      ],
    },
  ]);

  assert.deepEqual(
    items.map((item) => item.type),
    ["normal", "accordion"],
  );
});

test("duplicate call ids produce one tool group per turn", () => {
  const items = processMessages([
    toolMessage("FunctionCallContent", "user-1"),
    toolMessage("FunctionResultContent", "user-1"),
    toolMessage("FunctionCallContent", "user-2"),
    toolMessage("FunctionResultContent", "user-2"),
  ]);

  assert.equal(items.length, 2);
  assert.deepEqual(
    items.map((item) => item.type),
    ["accordion", "accordion"],
  );
  assert.deepEqual(
    items.map((item) =>
      item.type === "accordion" ? item.messages.map((message) => message.streamingScopeId) : [],
    ),
    [
      ["user-1", "user-1"],
      ["user-2", "user-2"],
    ],
  );
});

test("concurrent tool calls pair with out-of-order results in call order", () => {
  const items = processMessages([
    toolContentsMessage("FunctionCallContent", "user-1", ["call-1", "call-2", "call-3"]),
    toolContentsMessage("FunctionResultContent", "user-1", ["call-3", "call-1", "call-2"]),
  ]);

  assert.deepEqual(
    items.map((item) => item.type),
    ["accordion", "accordion", "accordion"],
  );
  assert.deepEqual(
    items.map((item) => (item.type === "accordion" ? item.toolName : "")),
    ["tool-call-1", "tool-call-2", "tool-call-3"],
  );
  assert.deepEqual(
    items.map((item) =>
      item.type === "accordion"
        ? item.messages.map((message) => message.contents[0].additionalProperties?.callId)
        : [],
    ),
    [
      ["call-1", "call-1"],
      ["call-2", "call-2"],
      ["call-3", "call-3"],
    ],
  );
});

test("final result messages keep their result classification", () => {
  const finalResult: AiMessage = {
    messageId: "final-result",
    author: "agent",
    role: "assistant",
    contents: [{ type: "TextContent", content: "done" }],
    additionalProperties: { type: "result" },
  };

  const items = processMessages([finalResult]);

  assert.deepEqual(items, [{ type: "result", message: finalResult }]);
});

test("mixed ordinary and unmatched tool contents preserve content order", () => {
  const message: AiMessage = {
    messageId: "mixed-message",
    author: "agent",
    role: "assistant",
    streamingScopeId: "user-1",
    contents: [
      { type: "TextContent", content: "before" },
      {
        type: "FunctionCallContent",
        content: "call",
        additionalProperties: { callId: "call-without-result", toolName: "orphan-call" },
      },
      { type: "TextContent", content: "after" },
      {
        type: "FunctionResultContent",
        content: "result",
        additionalProperties: { callId: "result-without-call" },
      },
    ],
  };

  const items = processMessages([message]);

  assert.deepEqual(
    items.map((item) => item.type),
    ["normal", "normal", "normal", "normal"],
  );
  assert.deepEqual(
    items.map((item) =>
      item.type === "normal" ? item.message.contents.map((content) => content.type) : [],
    ),
    [["TextContent"], ["FunctionCallContent"], ["TextContent"], ["FunctionResultContent"]],
  );
});
