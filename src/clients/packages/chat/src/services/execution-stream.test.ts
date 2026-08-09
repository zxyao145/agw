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
  source = `const MessageContentType = { TextContent: "TextContent" };\n${source}`;

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
    markdown: "# Plan\n\n1. Inspect",
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
