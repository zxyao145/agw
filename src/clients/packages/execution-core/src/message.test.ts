import assert from "node:assert/strict";
import test from "node:test";

import type { ExecutionMessage } from "./types";
import {
  cloneMessage,
  getMessageStreamingScopeId,
  getMessageTextContent,
  mergeStreamingMessage,
  mergeStreamingMessages,
  replaceStreamingScope,
  scopeMessagesByUserTurn,
  scopeStreamingMessage,
} from "./message";

function textMessage({
  messageId,
  role,
  author,
  content,
  streamingScopeId,
  additionalProperties,
}: {
  messageId: string;
  role: string;
  author: string | null;
  content: string;
  streamingScopeId?: string;
  additionalProperties?: Record<string, unknown>;
}): ExecutionMessage {
  return {
    messageId,
    role,
    author,
    contents: [{ type: "TextContent", content }],
    streamingScopeId,
    additionalProperties,
  };
}

test("history assigns a reconstructable scope per user turn", () => {
  const history = scopeMessagesByUserTurn([
    textMessage({ messageId: "user-1", role: "user", author: "$agw", content: "one" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "codex", content: "1" }),
    textMessage({ messageId: "user-2", role: "user", author: "$agw", content: "two" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "codex", content: "2" }),
    textMessage({ messageId: "user-3", role: "user", author: "$agw", content: "three" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "codex", content: "3" }),
  ]);

  assert.deepEqual(
    history.map((message) => message.streamingScopeId),
    ["user-1", "user-1", "user-2", "user-2", "user-3", "user-3"],
  );
});

test("history preserves explicit persisted scopes before inferring a user turn", () => {
  const history = scopeMessagesByUserTurn([
    textMessage({ messageId: "user-1", role: "user", author: "$agw", content: "one" }),
    textMessage({
      messageId: "item_0",
      role: "assistant",
      author: "agent",
      content: "restored",
      additionalProperties: { streamingScopeId: "persisted-scope" },
    }),
  ]);

  assert.deepEqual(
    history.map((message) => message.streamingScopeId),
    ["user-1", "persisted-scope"],
  );
});

test("Claude tool results persisted as pseudo-user messages stay in the active user turn", () => {
  const history = scopeMessagesByUserTurn([
    textMessage({ messageId: "user-1", role: "user", author: "$agw", content: "run" }),
    {
      messageId: "call-1",
      role: "assistant",
      author: "agent",
      contents: [
        {
          type: "FunctionCallContent",
          content: "{}",
          additionalProperties: { callId: "Bash_0", toolName: "Bash" },
        },
      ],
    },
    {
      messageId: "result-1",
      role: "user",
      author: null,
      additionalProperties: { modelHistoryExcluded: true },
      contents: [
        {
          type: "FunctionResultContent",
          content: "{}",
          additionalProperties: { callId: "Bash_0" },
        },
      ],
    },
  ]);

  assert.deepEqual(
    history.map((message) => message.streamingScopeId),
    ["user-1", "user-1", "user-1"],
  );
});

test("repeated message ids remain independent across turns", () => {
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
      .filter((message) => message.messageId === "item_0")
      .map((message) => message.streamingScopeId),
    ["user-1", "user-2", "user-3"],
  );
});

test("text deltas merge only when scope, id, role, and author all match", () => {
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

test("reasoning deltas merge into one reasoning content", () => {
  const merged = mergeStreamingMessages(
    [
      {
        messageId: "reasoning-1",
        role: "assistant",
        author: "agent",
        streamingScopeId: "user-1",
        contents: [{ type: "TextReasoningContent", content: "The" }],
      },
    ],
    [
      {
        messageId: "reasoning-1",
        role: "assistant",
        author: "agent",
        streamingScopeId: "user-1",
        contents: [{ type: "TextReasoningContent", content: " user" }],
      },
    ],
  );

  assert.equal(merged.length, 1);
  assert.deepEqual(merged[0].contents, [
    { type: "TextReasoningContent", content: "The user", additionalProperties: undefined },
  ]);
});

test("adjacent reasoning contents in the first streamed message are normalized", () => {
  const merged = mergeStreamingMessages(
    [],
    [
      {
        messageId: "reasoning-1",
        role: "assistant",
        author: "agent",
        streamingScopeId: "user-1",
        contents: [
          { type: "TextReasoningContent", content: "Simple" },
          { type: "TextReasoningContent", content: " math" },
        ],
      },
    ],
  );

  assert.deepEqual(merged[0].contents, [
    { type: "TextReasoningContent", content: "Simple math", additionalProperties: undefined },
  ]);
});

test("a streaming batch builds one result while preserving untouched message references", () => {
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

test("a tail delta skips the full identity index", () => {
  const originalStringify = JSON.stringify;
  let stringifyCalls = 0;
  JSON.stringify = ((...args: Parameters<typeof JSON.stringify>) => {
    stringifyCalls += 1;
    return originalStringify(...args);
  }) as typeof JSON.stringify;

  try {
    const merged = mergeStreamingMessage(
      [
        textMessage({
          messageId: "active",
          role: "assistant",
          author: "agent",
          content: "a",
          streamingScopeId: "user-1",
        }),
      ],
      textMessage({
        messageId: "active",
        role: "assistant",
        author: "agent",
        content: "b",
        streamingScopeId: "user-1",
      }),
    );

    assert.equal(merged[0].contents[0].content, "ab");
    assert.equal(stringifyCalls, 0);
  } finally {
    JSON.stringify = originalStringify;
  }
});

test("a non-tail delta falls back to the identity index", () => {
  const merged = mergeStreamingMessage(
    [
      textMessage({
        messageId: "earlier",
        role: "assistant",
        author: "agent",
        content: "a",
        streamingScopeId: "user-1",
      }),
      textMessage({
        messageId: "tail",
        role: "assistant",
        author: "agent",
        content: "stable",
        streamingScopeId: "user-1",
      }),
    ],
    textMessage({
      messageId: "earlier",
      role: "assistant",
      author: "agent",
      content: "b",
      streamingScopeId: "user-1",
    }),
  );

  assert.equal(merged[0].contents[0].content, "ab");
  assert.equal(merged[1].contents[0].content, "stable");
});

test("a streaming batch keeps duplicate ids isolated by scope", () => {
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
    merged.map((message) => message.contents[0].content),
    ["one", "two"],
  );
});

test("streaming contents preserve text and tool ordering", () => {
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
    merged[0].contents.map((content) => [content.type, content.content]),
    [
      ["TextContent", "before"],
      ["FunctionCallContent", undefined],
      ["TextContent", "after"],
    ],
  );
});

test("messages before the first user receive independent fallback scopes", () => {
  const history = scopeMessagesByUserTurn([
    textMessage({ messageId: "item_0", role: "assistant", author: "agent", content: "a" }),
    textMessage({ messageId: "item_0", role: "assistant", author: "agent", content: "b" }),
    textMessage({ messageId: "user-1", role: "user", author: "$agw", content: "one" }),
  ]);

  assert.notEqual(history[0].streamingScopeId, history[1].streamingScopeId);
  assert.equal(history[2].streamingScopeId, "user-1");
});

test("the persisted streaming scope takes precedence over the top-level field", () => {
  const message = {
    messageId: "item_0",
    role: "assistant",
    author: "agent",
    streamingScopeId: "stale",
    contents: [],
    additionalProperties: { streamingScopeId: "persisted" },
  };

  assert.equal(getMessageStreamingScopeId(message), "persisted");
});

test("scopeStreamingMessage returns an independent copy", () => {
  const message = textMessage({
    messageId: "item_0",
    role: "assistant",
    author: "agent",
    content: "hello",
  });

  const scoped = scopeStreamingMessage(message, "user-1");

  assert.equal(scoped.streamingScopeId, "user-1");
  assert.notEqual(scoped, message);
  assert.equal(message.streamingScopeId, undefined);
});

test("getMessageTextContent joins only text content", () => {
  const message: ExecutionMessage = {
    messageId: "item_0",
    role: "assistant",
    author: "agent",
    contents: [
      { type: "TextContent", content: "hello" },
      { type: "FunctionCallContent", content: "{}" },
      { type: "text", content: " world" },
    ],
  };

  assert.equal(getMessageTextContent(message), "hello");
});

test("cloneMessage deep copies contents and additionalProperties", () => {
  const message: ExecutionMessage = {
    messageId: "item_0",
    role: "assistant",
    author: "agent",
    contents: [{ type: "TextContent", content: "hello", additionalProperties: { a: 1 } }],
    additionalProperties: { b: 2 },
  };

  const cloned = cloneMessage(message);

  assert.notEqual(cloned, message);
  assert.notEqual(cloned.contents, message.contents);
  assert.notEqual(cloned.contents[0], message.contents[0]);
  assert.notEqual(cloned.additionalProperties, message.additionalProperties);
  assert.deepEqual(cloned, message);
});

test("replaces one streaming scope without disturbing other turns", () => {
  const previousTurn = textMessage({
    messageId: "item_0",
    role: "assistant",
    author: "agent",
    content: "previous",
    streamingScopeId: "user-0",
  });
  const persistedUser = textMessage({
    messageId: "user-1",
    role: "user",
    author: "$agw",
    content: "run",
    streamingScopeId: "user-1",
  });
  const persistedOutput = textMessage({
    messageId: "item_0",
    role: "assistant",
    author: null,
    content: "persisted",
    streamingScopeId: "user-1",
  });
  const liveUser = textMessage({
    messageId: "user-1",
    role: "user",
    author: "$agw",
    content: "run",
    streamingScopeId: "user-1",
  });
  const liveOutput = textMessage({
    messageId: "live-item",
    role: "assistant",
    author: "agent",
    content: "live",
    streamingScopeId: "user-1",
  });
  const history = [previousTurn, persistedUser, persistedOutput];

  const replaced = replaceStreamingScope(history, [liveUser, liveOutput], "user-1");

  assert.deepEqual(
    replaced.map((message) => message.contents[0]?.content),
    ["previous", "run", "live"],
  );
  assert.deepEqual(
    history.map((message) => message.contents[0]?.content),
    ["previous", "run", "persisted"],
  );
});

test("replaces only the requested scope when message ids repeat", () => {
  const messages = [
    textMessage({
      messageId: "item_0",
      role: "assistant",
      author: "agent",
      content: "first",
      streamingScopeId: "user-1",
    }),
    textMessage({
      messageId: "item_0",
      role: "assistant",
      author: "agent",
      content: "second",
      streamingScopeId: "user-2",
    }),
  ];

  const replaced = replaceStreamingScope(
    messages,
    [
      textMessage({
        messageId: "item-1",
        role: "assistant",
        author: "agent",
        content: "updated",
        streamingScopeId: "user-1",
      }),
    ],
    "user-1",
  );

  assert.deepEqual(
    replaced.map((message) => message.contents[0]?.content),
    ["updated", "second"],
  );
});
