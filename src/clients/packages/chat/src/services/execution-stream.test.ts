import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { parseProposedPlan } from "../ui-web/components/message/proposed-plan";
import {
  createStreamingMessageBatcher,
  createUserMessage,
  createUserTextMessage,
  mergeStreamingMessage,
  STREAMING_MESSAGE_BATCH_INTERVAL_MS,
  toExecutionUserInput,
} from "./execution-stream";

const EXECUTION_STREAM_URL = new URL("./execution-stream.ts", import.meta.url);

function textMessage({
  messageId,
  role,
  author,
  content,
  streamingScopeId,
}: {
  messageId: string;
  role: string;
  author: string;
  content: string;
  streamingScopeId?: string;
}): AiMessage {
  return {
    messageId,
    role,
    author,
    contents: [{ type: "TextContent", content }],
    streamingScopeId,
  };
}

test("execution stream re-exports platform-neutral helpers from execution-core", async () => {
  const source = await readFile(EXECUTION_STREAM_URL, "utf8");

  assert.match(source, /from "@agw\/execution-core"/);
  assert.doesNotMatch(source, /execution-ws|WebSocket|parseExecutionWsMessage/);
  assert.match(source, /mergeStreamingMessages,/);
  assert.match(source, /scopeMessagesByUserTurn,/);
});

test("streamed proposed plan tags merge into one restorable Plan Card payload", () => {
  const first = textMessage({
    messageId: "plan-1",
    role: "assistant",
    author: "agent",
    content: "<proposed_plan>\n# Pl",
    streamingScopeId: "user-1",
  });
  const withBody = mergeStreamingMessage(
    [first],
    textMessage({
      messageId: "plan-1",
      role: "assistant",
      author: "agent",
      content: "an\n\n1. Inspect\n</proposed_",
      streamingScopeId: "user-1",
    }),
  );
  const completed = mergeStreamingMessage(
    withBody,
    textMessage({
      messageId: "plan-1",
      role: "assistant",
      author: "agent",
      content: "plan>",
      streamingScopeId: "user-1",
    }),
  );

  assert.equal(completed.length, 1);
  assert.deepEqual(parseProposedPlan(completed[0].contents[0].content as string), {
    leadingMarkdown: "",
    markdown: "# Plan\n\n1. Inspect",
    trailingMarkdown: "",
    isClosed: true,
  });
});

test("the 50ms batcher commits a burst once and drops an old generation", async () => {
  const scheduled: Array<() => void> = [];
  const flushed: Array<{ messages: unknown[]; generation: number }> = [];
  const batcher = createStreamingMessageBatcher(
    (messages: unknown[], generation: number) => flushed.push({ messages, generation }),
    (callback: () => void, delay: number) => {
      assert.equal(delay, 50);
      scheduled.push(callback);
      return scheduled.length;
    },
    () => undefined,
  );

  assert.equal(STREAMING_MESSAGE_BATCH_INTERVAL_MS, 50);
  for (let index = 0; index < 100; index += 1) {
    batcher.enqueue(
      textMessage({
        messageId: "active",
        role: "assistant",
        author: "agent",
        content: String(index),
        streamingScopeId: "user-1",
      }),
      1,
    );
  }
  assert.equal(scheduled.length, 1);
  scheduled[0]();
  assert.equal(flushed.length, 1);
  assert.equal(flushed[0].messages.length, 100);

  batcher.enqueue(
    textMessage({
      messageId: "stale",
      role: "assistant",
      author: "agent",
      content: "stale",
      streamingScopeId: "user-1",
    }),
    1,
  );
  batcher.enqueue(
    textMessage({
      messageId: "current",
      role: "assistant",
      author: "agent",
      content: "current",
      streamingScopeId: "user-2",
    }),
    2,
  );
  batcher.flush(2);

  assert.equal(flushed.length, 2);
  assert.equal((flushed[1].messages[0] as { messageId: string }).messageId, "current");
});

test("standalone whitespace deltas preserve fenced plan Markdown exactly", () => {
  const chunks = [
    "Intro",
    "\n",
    "<proposed_plan>",
    "\n",
    "```sh",
    "\n",
    "fi",
    "\n",
    "```",
    "\n",
    "</proposed_plan>",
  ];
  let messages: ReturnType<typeof textMessage>[] = [];

  for (const content of chunks) {
    messages = mergeStreamingMessage(
      messages,
      textMessage({
        messageId: "plan-with-fence",
        role: "assistant",
        author: "agent",
        content,
        streamingScopeId: "user-1",
      }),
    );
  }

  const content = messages[0].contents[0].content as string;
  assert.equal(content, chunks.join(""));
  assert.deepEqual(parseProposedPlan(content), {
    leadingMarkdown: "Intro",
    markdown: "```sh\nfi\n```",
    trailingMarkdown: "",
    isClosed: true,
  });
});
test("the local streaming scope is excluded from execution input", () => {
  const input = toExecutionUserInput(
    textMessage({
      messageId: "user-1",
      role: "user",
      author: "$agw",
      content: "one",
      streamingScopeId: "user-1",
    }),
  );

  assert.deepEqual(input, {
    messageId: "user-1",
    author: "$agw",
    contents: [{ type: "TextContent", content: "one", additionalProperties: undefined }],
  });
  assert.equal("streamingScopeId" in input, false);
});

test("createUserTextMessage creates an authored user text message", () => {
  const message = createUserTextMessage("hello");

  assert.equal(message.role, "user");
  assert.equal(message.author, "$agw");
  assert.equal(message.contents.length, 1);
  assert.equal(message.contents[0].type, "TextContent");
  assert.equal(message.contents[0].content, "hello");
  assert.equal(typeof message.messageId, "string");
});

test("createUserMessage puts images before optional text", () => {
  const message = createUserMessage("describe this", [
    {
      id: "image-1",
      name: "screen.png",
      mediaType: "image/png",
      size: 3,
      dataUrl: "data:image/png;base64,AQID",
    },
  ]);

  assert.deepEqual(message.contents, [
    { type: "DataContent", uri: "data:image/png;base64,AQID", name: "screen.png" },
    { type: "TextContent", content: "describe this" },
  ]);
});

test("createUserMessage supports image-only input and preserves data fields for execution", () => {
  const message = createUserMessage("", [
    {
      id: "image-1",
      name: "screen.webp",
      mediaType: "image/webp",
      size: 1,
      dataUrl: "data:image/webp;base64,AQ==",
    },
  ]);

  assert.deepEqual(toExecutionUserInput(message).contents, [
    {
      type: "DataContent",
      uri: "data:image/webp;base64,AQ==",
      name: "screen.webp",
      additionalProperties: undefined,
    },
  ]);
});
