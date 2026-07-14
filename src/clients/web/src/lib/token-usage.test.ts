import assert from "node:assert/strict";
import test from "node:test";

const modulePath = "./token-usage.ts";

async function loadTokenUsageModule() {
  const module = await import(modulePath).catch(() => null);
  assert.ok(module, "token usage utilities should exist");
  return module;
}

test("normalizeTokenUsage converts supported values and rejects invalid counts", async () => {
  const { normalizeTokenUsage } = await loadTokenUsageModule();

  assert.deepEqual(
    normalizeTokenUsage({
      inputTokenCount: "12",
      outputTokenCount: 5,
      totalTokenCount: null,
      cachedInputTokenCount: -3,
      reasoningTokenCount: "invalid",
    }),
    {
      inputTokenCount: 12,
      outputTokenCount: 5,
      totalTokenCount: 0,
      cachedInputTokenCount: 0,
      reasoningTokenCount: 0,
    },
  );
});

test("addTokenUsage sums all five counters without deriving total", async () => {
  const { addTokenUsage } = await loadTokenUsageModule();

  assert.deepEqual(
    addTokenUsage(
      {
        inputTokenCount: 10,
        outputTokenCount: 20,
        totalTokenCount: 40,
        cachedInputTokenCount: 3,
        reasoningTokenCount: 4,
      },
      {
        inputTokenCount: 1,
        outputTokenCount: 2,
        totalTokenCount: 5,
        cachedInputTokenCount: 6,
        reasoningTokenCount: 7,
      },
    ),
    {
      inputTokenCount: 11,
      outputTokenCount: 22,
      totalTokenCount: 45,
      cachedInputTokenCount: 9,
      reasoningTokenCount: 11,
    },
  );
});

test("getMessageTokenUsage combines every usage content and distinguishes missing usage", async () => {
  const { getMessageTokenUsage } = await loadTokenUsageModule();
  const message = {
    messageId: "message-1",
    role: "assistant",
    contents: [
      { type: "TextContent", content: "Done" },
      {
        type: "UsageContent",
        content: {
          inputTokenCount: 10,
          outputTokenCount: 5,
          totalTokenCount: 15,
          cachedInputTokenCount: 2,
          reasoningTokenCount: 1,
        },
      },
      {
        type: "UsageContent",
        content: {
          inputTokenCount: "3",
          outputTokenCount: "4",
          totalTokenCount: "7",
          cachedInputTokenCount: "0",
          reasoningTokenCount: "2",
        },
      },
    ],
  };

  assert.deepEqual(getMessageTokenUsage(message), {
    inputTokenCount: 13,
    outputTokenCount: 9,
    totalTokenCount: 22,
    cachedInputTokenCount: 2,
    reasoningTokenCount: 3,
  });
  assert.equal(
    getMessageTokenUsage({
      messageId: "message-2",
      contents: [{ type: "TextContent", content: "No usage" }],
    }),
    null,
  );
  assert.deepEqual(
    getMessageTokenUsage({
      messageId: "message-3",
      contents: [{ type: "UsageContent", content: {} }],
    }),
    {
      inputTokenCount: 0,
      outputTokenCount: 0,
      totalTokenCount: 0,
      cachedInputTokenCount: 0,
      reasoningTokenCount: 0,
    },
  );
});

test("stripUsageContents preserves mixed messages and removes usage-only messages", async () => {
  const { stripUsageContents } = await loadTokenUsageModule();
  const messages = [
    {
      messageId: "mixed",
      role: "assistant",
      contents: [
        { type: "TextContent", content: "Visible" },
        { type: "UsageContent", content: { totalTokenCount: 3 } },
      ],
    },
    {
      messageId: "usage-only",
      role: "assistant",
      contents: [{ type: "UsageContent", content: { totalTokenCount: 3 } }],
    },
    {
      messageId: "plain",
      role: "user",
      contents: [{ type: "TextContent", content: "Keep me" }],
    },
  ];

  assert.deepEqual(stripUsageContents(messages), [
    {
      messageId: "mixed",
      role: "assistant",
      contents: [{ type: "TextContent", content: "Visible" }],
    },
    messages[2],
  ]);
  assert.equal(messages[0].contents.length, 2);
});

test("formatTokenCount uses full, K, and M display thresholds", async () => {
  const { formatTokenCount } = await loadTokenUsageModule();

  assert.equal(formatTokenCount(0), "0");
  assert.equal(formatTokenCount(9_999), (9_999).toLocaleString());
  assert.equal(formatTokenCount(10_000), "10K");
  assert.equal(formatTokenCount(12_500), "12.5K");
  assert.equal(formatTokenCount(1_000_000), "1M");
  assert.equal(formatTokenCount(1_250_000), "1.3M");
});
