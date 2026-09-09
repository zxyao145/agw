import assert from "node:assert/strict";
import test from "node:test";
import type { AiMessage } from "@agw/api";
import type { InteractionRequest } from "@agw/execution-core";
import { getPendingInteraction, parseSimpleUserInput } from "./human-interaction";
import { buildConversationRenderModel, isHiddenControlMessage } from "./conversation-render-model";
import { prepareClaudeHistory } from "./message-presentation";

function message(interaction: unknown, type = "interaction-request"): AiMessage {
  return {
    messageId: "interaction-message",
    role: "system",
    contents: [],
    additionalProperties: {
      type,
      interaction,
      executionId: "execution-1",
      streamingScopeId: "turn-1",
    },
  };
}

const requests: InteractionRequest[] = [
  {
    kind: "tool-approval",
    interactionId: "tool-1",
    prompt: "Run tool?",
    source: { toolName: "shell", callId: "call-1" },
    arguments: { command: "pwd", flags: [1, null] },
  },
  {
    kind: "workflow-gate",
    interactionId: "gate-1",
    prompt: "Continue?",
    source: { nodeId: "review", nodeName: "Review", providerScopeId: "workflow-port-1" },
    mode: "input",
    inputPreview: "Draft",
  },
  {
    kind: "user-input",
    interactionId: "input-1",
    prompt: "Choose mode",
    source: { nodeId: "agent", providerRequestId: "provider-1" },
    inputKind: "mode-change",
    payload: { mode: "plan" },
  },
];

for (const request of requests) {
  test(`parses ${request.kind} with its source, JSON and execution identity`, () => {
    const pending = getPendingInteraction(message(request));
    assert.deepEqual(pending, {
      ...request,
      executionId: "execution-1",
      streamingScopeId: "turn-1",
      ...(request.kind === "user-input" ? { modeChange: { mode: "plan" } } : {}),
    });
  });
}

test("provider scope stays optional and is never inferred from the business node ID", () => {
  for (const source of [
    { nodeId: "business-node" },
    { providerScopeId: "sdk-request-port" },
    {
      nodeId: "business-node",
      providerScopeId: "sdk-request-port",
      providerRequestId: "request-1",
    },
  ]) {
    const pending = getPendingInteraction(message({ ...requests[1], source }));
    assert.deepEqual(pending?.source, source);
  }
});

for (const type of ["human-gate-request", "tool-approval-request", "human-interaction-request"]) {
  test(`does not parse the retired ${type} request`, () => {
    assert.equal(getPendingInteraction(message(requests[0], type)), null);
  });
}

test("rejects missing identity, malformed source and mismatched request variants", () => {
  const valid = requests[0];
  for (const interaction of [
    { ...valid, interactionId: "" },
    { ...valid, source: null },
    { ...valid, prompt: 42 },
    { ...valid, kind: "unknown" },
    { ...valid, kind: "workflow-gate" },
    { ...valid, kind: "user-input", inputKind: "input" },
  ])
    assert.equal(getPendingInteraction(message(interaction)), null);
});

test("tool requests need no node ID and preserve JSON null arguments", () => {
  const pending = getPendingInteraction(message({ ...requests[0], source: {}, arguments: null }));
  assert.equal(pending?.kind, "tool-approval");
  assert.deepEqual(pending?.source, {});
  assert.equal(pending?.kind === "tool-approval" && pending.arguments, null);
});

for (const inputKind of ["confirm", "select", "input", "editor"]) {
  test(`preserves Pi ${inputKind} payload and reads SDK field names`, () => {
    const payload = {
      Message: "Details",
      Options: ["A", "B"],
      Placeholder: "Answer",
      Prefill: "  existing\ntext  ",
    };
    const pending = getPendingInteraction(
      message({
        kind: "user-input",
        interactionId: "pi-1",
        prompt: "Pi",
        source: {},
        inputKind,
        payload,
      }),
    );
    assert.equal(pending?.kind, "user-input");
    assert.deepEqual(pending?.kind === "user-input" && pending.payload, payload);
    assert.deepEqual(parseSimpleUserInput(inputKind, payload), {
      inputKind,
      message: "Details",
      options: ["A", "B"],
      placeholder: "Answer",
      prefill: "  existing\ntext  ",
    });
  });
}

test("request controls stay hidden in history while pending cards use interaction ID", () => {
  const incoming = message(requests[0]);
  assert.equal(isHiddenControlMessage(incoming), true);
  assert.deepEqual(prepareClaudeHistory([incoming]).messages, []);
  const items = buildConversationRenderModel([incoming], {
    pendingInteraction: getPendingInteraction(incoming),
  });
  assert.equal(items.length, 1);
  assert.equal(items[0]?.type, "human-interaction");
  assert.equal(items[0]?.type === "human-interaction" && items[0].request.interactionId, "tool-1");
});
