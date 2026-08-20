import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import ts from "typescript";
import { parseProposedPlan } from "../ui-web/components/message/proposed-plan";

const EXECUTION_STREAM_URL = new URL("./execution-stream.ts", import.meta.url);

async function loadExecutionStream() {
  let source = await readFile(EXECUTION_STREAM_URL, "utf8");
  source = source.replace(
    'import { createUuidV7 } from "@agw/api";',
    'const createUuidV7 = () => "generated-user-id";',
  );
  source = source.replace(/import \{[\s\S]*?\} from "@\/types";/, "");
  source = `const MessageContentType = { DataContent: "DataContent", TextContent: "TextContent" };\n${source}`;

  const javascript = ts.transpileModule(source, {
    compilerOptions: {
      module: ts.ModuleKind.ES2022,
      target: ts.ScriptTarget.ES2022,
    },
  }).outputText;

  return import(`data:text/javascript;base64,${Buffer.from(javascript).toString("base64")}`);
}

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
}) {
  return {
    messageId,
    role,
    author,
    contents: [{ type: "TextContent", content }],
    streamingScopeId,
  };
}

test("execution stream contains only transport-neutral helpers", async () => {
  const source = await readFile(EXECUTION_STREAM_URL, "utf8");

  assert.match(source, /export function toExecutionUserInput\(/);
  assert.doesNotMatch(source, /execution-ws|WebSocket|parseExecutionWsMessage/);
});

test("history assigns a reconstructable scope per user turn", async () => {
  const { scopeMessagesByUserTurn } = await loadExecutionStream();
  const history = scopeMessagesByUserTurn([
    textMessage({ messageId: "user-1", role: "user", author: "$agw", content: "one" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "codex", content: "1" }),
    textMessage({ messageId: "user-2", role: "user", author: "$agw", content: "two" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "codex", content: "2" }),
    textMessage({ messageId: "user-3", role: "user", author: "$agw", content: "three" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "codex", content: "3" }),
  ]);

  assert.deepEqual(
    history.map((message: { streamingScopeId?: string }) => message.streamingScopeId),
    ["user-1", "user-1", "user-2", "user-2", "user-3", "user-3"],
  );
});

test("repeated message ids remain independent across turns", async () => {
  const { mergeStreamingMessage, scopeMessagesByUserTurn } = await loadExecutionStream();
  const history = scopeMessagesByUserTurn([
    textMessage({ messageId: "user-1", role: "user", author: "$agw", content: "one" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "codex", content: "1" }),
    textMessage({ messageId: "user-2", role: "user", author: "$agw", content: "two" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "codex", content: "2" }),
  ]);
  const messages = mergeStreamingMessage(
    [
      ...history,
      textMessage({
        messageId: "user-3",
        role: "user",
        author: "$agw",
        content: "three",
        streamingScopeId: "user-3",
      }),
    ],
    textMessage({
      messageId: "item_0",
      role: "assistant",
      author: "codex",
      content: "3",
      streamingScopeId: "user-3",
    }),
  );

  assert.deepEqual(
    messages
      .filter((message: { messageId: string }) => message.messageId === "item_0")
      .map((message: { streamingScopeId?: string }) => message.streamingScopeId),
    ["user-1", "user-2", "user-3"],
  );
});

test("text deltas merge only when scope, id, role, and author all match", async () => {
  const { mergeStreamingMessage } = await loadExecutionStream();
  const first = textMessage({
    messageId: "item_0",
    role: "assistant",
    author: "agent-a",
    content: "hel",
    streamingScopeId: "user-1",
  });
  const merged = mergeStreamingMessage(
    [first],
    textMessage({
      messageId: "item_0",
      role: "assistant",
      author: "agent-a",
      content: "lo",
      streamingScopeId: "user-1",
    }),
  );
  const differentRole = mergeStreamingMessage(
    merged,
    textMessage({
      messageId: "item_0",
      role: "tool",
      author: "agent-a",
      content: "tool",
      streamingScopeId: "user-1",
    }),
  );
  const differentAuthor = mergeStreamingMessage(
    differentRole,
    textMessage({
      messageId: "item_0",
      role: "assistant",
      author: "agent-b",
      content: "other",
      streamingScopeId: "user-1",
    }),
  );

  assert.equal(merged.length, 1);
  assert.equal(merged[0].contents[0].content, "hello");
  assert.equal(differentAuthor.length, 3);
});

test("a streaming batch builds one result while preserving untouched message references", async () => {
  const { mergeStreamingMessages } = await loadExecutionStream();
  const untouched = textMessage({
    messageId: "untouched",
    role: "assistant",
    author: "agent",
    content: "stable",
    streamingScopeId: "user-1",
  });
  const active = textMessage({
    messageId: "active",
    role: "assistant",
    author: "agent",
    content: "a",
    streamingScopeId: "user-1",
  });
  const original = [untouched, active];
  const merged = mergeStreamingMessages(original, [
    textMessage({
      messageId: "active",
      role: "assistant",
      author: "agent",
      content: "b",
      streamingScopeId: "user-1",
    }),
    textMessage({
      messageId: "active",
      role: "assistant",
      author: "agent",
      content: "c",
      streamingScopeId: "user-1",
    }),
  ]);

  assert.equal(merged[0], untouched);
  assert.notEqual(merged[1], active);
  assert.equal(merged[1].contents[0].content, "abc");
  assert.equal(active.contents[0].content, "a");
});

test("a streaming batch keeps duplicate ids isolated by scope", async () => {
  const { mergeStreamingMessages } = await loadExecutionStream();
  const merged = mergeStreamingMessages(
    [],
    [
      textMessage({
        messageId: "item_0",
        role: "assistant",
        author: "agent",
        content: "one",
        streamingScopeId: "user-1",
      }),
      textMessage({
        messageId: "item_0",
        role: "assistant",
        author: "agent",
        content: "two",
        streamingScopeId: "user-2",
      }),
    ],
  );

  assert.deepEqual(
    merged.map((message: { contents: Array<{ content?: string }> }) => message.contents[0].content),
    ["one", "two"],
  );
});

test("streaming contents preserve text and tool ordering", async () => {
  const { mergeStreamingMessages } = await loadExecutionStream();
  const merged = mergeStreamingMessages(
    [
      textMessage({
        messageId: "item_0",
        role: "assistant",
        author: "agent",
        content: "before",
        streamingScopeId: "user-1",
      }),
    ],
    [
      {
        messageId: "item_0",
        role: "assistant",
        author: "agent",
        streamingScopeId: "user-1",
        contents: [{ type: "FunctionCallContent", callId: "call-1", name: "tool" }],
      },
      textMessage({
        messageId: "item_0",
        role: "assistant",
        author: "agent",
        content: "after",
        streamingScopeId: "user-1",
      }),
    ],
  );

  assert.deepEqual(
    merged[0].contents.map((content: { type: string; content?: string }) => [
      content.type,
      content.content,
    ]),
    [
      ["TextContent", "before"],
      ["FunctionCallContent", undefined],
      ["TextContent", "after"],
    ],
  );
});

test("the 50ms batcher commits a burst once and drops an old generation", async () => {
  const { createStreamingMessageBatcher, STREAMING_MESSAGE_BATCH_INTERVAL_MS } =
    await loadExecutionStream();
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

test("streamed proposed plan tags merge into one restorable Plan Card payload", async () => {
  const { mergeStreamingMessage } = await loadExecutionStream();
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

test("standalone whitespace deltas preserve fenced plan Markdown exactly", async () => {
  const { mergeStreamingMessage } = await loadExecutionStream();
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

test("messages before the first user receive independent fallback scopes", async () => {
  const { scopeMessagesByUserTurn } = await loadExecutionStream();
  const history = scopeMessagesByUserTurn([
    textMessage({ messageId: "item_0", role: "assistant", author: "agent", content: "a" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "agent", content: "b" }),
    textMessage({ messageId: "user-1", role: "user", author: "$agw", content: "one" }),
  ]);

  assert.notEqual(history[0].streamingScopeId, history[1].streamingScopeId);
  assert.equal(history[2].streamingScopeId, "user-1");
});

test("the local streaming scope is excluded from execution input", async () => {
  const { toExecutionUserInput } = await loadExecutionStream();
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

test("createUserTextMessage creates an authored user text message", async () => {
  const { createUserTextMessage } = await loadExecutionStream();

  assert.deepEqual(createUserTextMessage("hello"), {
    messageId: "generated-user-id",
    author: "$agw",
    role: "user",
    contents: [{ type: "TextContent", content: "hello" }],
  });
});

test("createUserMessage puts images before optional text", async () => {
  const { createUserMessage } = await loadExecutionStream();
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

test("createUserMessage supports image-only input and preserves data fields for execution", async () => {
  const { createUserMessage, toExecutionUserInput } = await loadExecutionStream();
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
