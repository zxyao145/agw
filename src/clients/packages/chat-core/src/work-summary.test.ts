import assert from "node:assert/strict";
import test from "node:test";
import type { AiMessage, ConversationHistoryTurn } from "@agw/api";
import { buildConversationRenderModel } from "./conversation-render-model";
import { formatWorkedDuration } from "./work-summary";

function message(id: string, role: string, createdAt?: string, result = false): AiMessage {
  return {
    messageId: id,
    role,
    createdAt,
    contents: [{ type: "TextContent", content: id }],
    additionalProperties: result ? { type: "result" } : undefined,
  };
}

function turn(id = "1"): AiMessage[] {
  return [
    message(`input-${id}`, "user", "2026-09-20T01:00:00Z"),
    message(`process-${id}`, "assistant"),
    message(`result-${id}`, "assistant", "2026-09-20T01:17:39Z", true),
  ];
}

test("completed work keeps inputs and every Result visible without mutating source data", () => {
  const messages = turn();
  messages.push(
    message("late-process", "assistant"),
    message("last-result", "system", "2026-09-20T01:20:00Z", true),
  );
  const original = structuredClone(messages);
  const items = buildConversationRenderModel(messages);
  assert.deepEqual(
    items.map((item) => item.type),
    ["message", "work-summary", "result", "result"],
  );
  const summary = items[1];
  assert.equal(summary.type, "work-summary");
  if (summary.type !== "work-summary") return;
  assert.equal(summary.durationMs, 1_200_000);
  assert.deepEqual(
    summary.items.map((item) =>
      item.type === "message" ? item.message.source.messageId : item.type,
    ),
    ["process-1", "late-process"],
  );
  assert.deepEqual(messages, original);
  assert.ok(items[2].type === "result" && items[2].hasWorkSummary);
});

test("the summary names the Agent of the first Result while the Result keeps no header", () => {
  const messages = turn();
  const unnamed = buildConversationRenderModel(messages)[1];
  assert.ok(unnamed.type === "work-summary");
  assert.equal(unnamed.name, null);

  messages[2].author = "claude-code";
  messages.push({
    ...message("node-result", "assistant", "2026-09-20T01:20:00Z", true),
    additionalProperties: { type: "result", nodeName: "Reviewer" },
  });
  const items = buildConversationRenderModel(messages);
  assert.ok(items[1].type === "work-summary");
  assert.equal(items[1].name, "claude-code");
  assert.ok(items[2].type === "result");
  assert.equal(items[2].message.meta, null);

  messages[2].additionalProperties = { type: "result", nodeName: "Planner" };
  const node = buildConversationRenderModel(messages)[1];
  assert.ok(node.type === "work-summary");
  assert.equal(node.name, "Planner");
});

test("a history page without process messages still names the summary from its Result", () => {
  const turnId = "turn-named";
  const input = {
    ...message("input", "user", "2026-09-20T01:00:00Z"),
    additionalProperties: { turnId },
  };
  const result = {
    ...message("result", "assistant", "2026-09-20T01:00:20Z", true),
    author: "claude-code",
    additionalProperties: { type: "result", turnId },
  };
  const items = buildConversationRenderModel([result], {
    historyTurns: [
      { turnId, status: "completed", input, results: [result], hasProcessMessages: true },
    ],
  });
  assert.deepEqual(
    items.map((item) => item.type),
    ["message", "work-summary", "result"],
  );
  assert.ok(items[1].type === "work-summary");
  assert.equal(items[1].items.length, 0);
  assert.equal(items[1].name, "claude-code");
});

test("active turns stay visible, including a Result received before completion", () => {
  const items = buildConversationRenderModel([...turn("1"), ...turn("2")], {
    isCurrentTurnActive: true,
  });
  assert.deepEqual(
    items.map((item) => item.type),
    ["message", "work-summary", "result", "message", "message", "result"],
  );
});

test("no Result, no process, or an incomplete history page produces no summary", () => {
  const messages = turn();
  for (const input of [messages.slice(0, 2), [messages[0], messages[2]], messages.slice(1)]) {
    assert.ok(buildConversationRenderModel(input).every((item) => item.type !== "work-summary"));
  }
  assert.equal(buildConversationRenderModel(messages)[1].type, "work-summary");
});

test("pending interaction keeps the current process and request visible even after a Result", () => {
  const items = buildConversationRenderModel(turn(), {
    pendingInteraction: {
      kind: "tool-approval",
      interactionId: "approval",
      prompt: "Approve this command?",
      source: {},
    },
  });
  assert.ok(items.every((item) => item.type !== "work-summary"));
  assert.equal(items.at(-1)?.type, "human-interaction");
});

test("multiple turns retain their own processes and stable keys when history is prepended", () => {
  const first = buildConversationRenderModel(turn("2"))[1];
  const items = buildConversationRenderModel([...turn("1"), ...turn("2")]);
  const summaries = items.filter((item) => item.type === "work-summary");
  assert.equal(summaries.length, 2);
  assert.notEqual(summaries[0].key, summaries[1].key);
  assert.equal(summaries[1].key, first.key);
  assert.equal(summaries[0].items.length, 1);
  assert.equal(summaries[1].items.length, 1);
});

test("a completed turn folds from a later history page and keeps its identity as pages arrive", () => {
  const turnId = "turn-1";
  const input = {
    ...message("input", "user", "2026-09-25T16:28:12.993Z"),
    additionalProperties: { turnId },
  };
  const early = { ...message("early", "assistant"), additionalProperties: { turnId } };
  const result = {
    ...message("result", "assistant", "2026-09-25T19:18:17.772Z", true),
    additionalProperties: { type: "result", turnId },
  };
  const late = { ...message("late", "assistant"), additionalProperties: { turnId } };
  const historyTurn: ConversationHistoryTurn = {
    turnId,
    status: "completed",
    input,
    results: [result],
    hasProcessMessages: true,
  };
  const options = { historyTurns: [historyTurn] };

  const latest = buildConversationRenderModel([late], options);
  assert.deepEqual(
    latest.map((item) => item.type),
    ["message", "work-summary", "result"],
  );
  assert.equal(latest[1].type, "work-summary");
  if (latest[1].type !== "work-summary") return;
  assert.equal(latest[1].durationMs, 10_204_779);
  assert.equal(formatWorkedDuration(latest[1].durationMs), "Worked for 2h 50m 4s");
  assert.equal(latest[1].key, `work-summary:${turnId}`);
  assert.deepEqual(
    latest[1].items.map((item) => item.type),
    ["message"],
  );

  const older = buildConversationRenderModel([early, result, late], options);
  const full = buildConversationRenderModel([input, early, result, late], options);
  assert.deepEqual(
    older.map((item) => item.type),
    latest.map((item) => item.type),
  );
  assert.deepEqual(
    full.map((item) => item.type),
    latest.map((item) => item.type),
  );
  for (const items of [older, full]) {
    const summary = items[1];
    assert.equal(summary.type, "work-summary");
    if (summary.type !== "work-summary") continue;
    assert.equal(summary.key, latest[1].key);
    assert.equal(summary.durationMs, latest[1].durationMs);
    assert.deepEqual(
      summary.items.map((item) => item.type === "message" && item.message.source.messageId),
      ["early", "late"],
    );
    assert.equal(items.filter((item) => item.type === "result").length, 1);
  }

  const active = buildConversationRenderModel([late], {
    ...options,
    isCurrentTurnActive: true,
  });
  assert.ok(active.every((item) => item.type !== "work-summary"));
});

test("paged turns keep ordered Results visible and do not fold empty work", () => {
  const turnId = "turn-2";
  const input = {
    ...message("input-2", "user", "2026-09-20T01:00:00Z"),
    additionalProperties: { turnId },
  };
  const result = {
    ...message("result-2", "assistant", "2026-09-20T01:05:00Z", true),
    additionalProperties: { type: "result", turnId },
  };
  const finalResult = {
    ...message("final-2", "assistant", "2026-09-20T01:07:00Z", true),
    additionalProperties: { type: "result", turnId },
  };
  const historyTurn: ConversationHistoryTurn = {
    turnId,
    status: "completed",
    input,
    results: [result, finalResult],
    hasProcessMessages: false,
  };
  const empty = buildConversationRenderModel([result], { historyTurns: [historyTurn] });
  assert.ok(empty.every((item) => item.type !== "work-summary"));

  const process = { ...message("process-2", "assistant"), additionalProperties: { turnId } };
  const items = buildConversationRenderModel([process, result], {
    historyTurns: [{ ...historyTurn, hasProcessMessages: true }],
  });
  assert.deepEqual(
    items.map((item) => item.type),
    ["message", "work-summary", "result", "result"],
  );
  assert.equal(items[1].type === "work-summary" && items[1].durationMs, 420_000);
  assert.deepEqual(
    items
      .filter((item) => item.type === "result")
      .map((item) => (item.type === "result" ? item.message.source.messageId : null)),
    ["result-2", "final-2"],
  );
});

test("node inputs stay inside their parent turn and hidden control messages stay hidden", () => {
  const messages = turn();
  messages.splice(
    1,
    0,
    { ...message("node-input", "user"), additionalProperties: { agentflowInput: true } },
    { ...message("control", "system"), additionalProperties: { type: "agw-turn-start" } },
  );
  const summary = buildConversationRenderModel(messages)[1];
  assert.ok(summary.type === "work-summary");
  assert.equal(summary.items.length, 2);
});

test("content-level Results fold work but function results alone do not", () => {
  const messages = turn();
  messages[2].additionalProperties = undefined;
  messages[2].contents[0].additionalProperties = { type: "result" };
  assert.equal(buildConversationRenderModel(messages)[1].type, "work-summary");
  messages[2] = {
    ...messages[2],
    contents: [{ type: "FunctionResultContent", content: "done" }],
  };
  assert.ok(buildConversationRenderModel(messages).every((item) => item.type !== "work-summary"));
});

test("cross-scope tool results still match their original call inside completed work", () => {
  const messages = turn();
  messages.splice(
    2,
    0,
    {
      ...message("call", "assistant"),
      streamingScopeId: "original",
      contents: [
        {
          type: "FunctionCallContent",
          content: "{}",
          additionalProperties: { callId: "call", toolName: "read_file" },
        },
      ],
    },
    {
      ...message("return", "tool"),
      streamingScopeId: "replay",
      contents: [
        {
          type: "FunctionResultContent",
          content: "done",
          additionalProperties: { callId: "call" },
        },
      ],
    },
  );
  const summary = buildConversationRenderModel(messages, { collapseToolRuns: true })[1];
  assert.ok(summary.type === "work-summary");
  const tool = summary.items.find((item) => item.type === "tool-accordion");
  assert.ok(tool?.type === "tool-accordion");
  assert.equal(tool.messages.length, 2);
});

test("timestamps use absolute instants, last Result order, and never substitute current time", () => {
  for (const [start, end, expected] of [
    ["2026-09-20T23:59:59Z", "2026-09-21T08:00:01+08:00", 2000],
    ["2026-09-20T00:00:00Z", "2026-09-20T00:00:00.999Z", 999],
    [undefined, "2026-09-20T00:00:00Z", null],
    ["invalid", "2026-09-20T00:00:00Z", null],
    ["2026-09-20T01:00:00Z", "2026-09-20T00:00:00Z", null],
  ] as const) {
    const messages = turn();
    messages[0].createdAt = start;
    messages[2].createdAt = end;
    const summary = buildConversationRenderModel(messages)[1];
    assert.ok(summary.type === "work-summary");
    assert.equal(summary.durationMs, expected);
  }
  const messages = turn();
  messages.push(message("last", "assistant", undefined, true));
  const summary = buildConversationRenderModel(messages)[1];
  assert.ok(summary.type === "work-summary");
  assert.equal(summary.durationMs, null);
});

test("Worked duration formats seconds, minutes, hours and unknown durations", () => {
  for (const [value, expected] of [
    [null, "Worked"],
    [NaN, "Worked"],
    [-1, "Worked"],
    [0, "Worked for 0s"],
    [999, "Worked for 0s"],
    [8000, "Worked for 8s"],
    [59999, "Worked for 59s"],
    [60000, "Worked for 1m 0s"],
    [1059000, "Worked for 17m 39s"],
    [3600000, "Worked for 1h 0m 0s"],
    [3723000, "Worked for 1h 2m 3s"],
    [90000000, "Worked for 25h 0m 0s"],
  ] as const)
    assert.equal(formatWorkedDuration(value), expected);
});
