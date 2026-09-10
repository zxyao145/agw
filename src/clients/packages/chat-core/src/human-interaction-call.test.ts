import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { hasMatchingHumanInteractionCall, matchesHumanInteractionCall } from "./human-interaction";

function functionCallMessage(callId: string, streamingScopeId: string): AiMessage {
  return {
    messageId: `${streamingScopeId}-${callId}`,
    role: "assistant",
    author: "agent",
    streamingScopeId,
    contents: [
      {
        type: "FunctionCallContent",
        content: "{}",
        additionalProperties: { callId, toolName: "ask_user_question" },
      },
    ],
  };
}

test("matchesHumanInteractionCall uses call id and streaming scope", () => {
  const currentCall = functionCallMessage("call-1", "turn-2");

  assert.equal(
    matchesHumanInteractionCall(currentCall, {
      source: { callId: "call-1" },
      streamingScopeId: "turn-2",
    }),
    true,
  );
  assert.equal(
    matchesHumanInteractionCall(currentCall, {
      source: { callId: "call-1" },
      streamingScopeId: "turn-1",
    }),
    false,
  );
});

test("hasMatchingHumanInteractionCall does not bind a repeated call id to an older turn", () => {
  const messages = [functionCallMessage("call-1", "turn-1")];

  assert.equal(
    hasMatchingHumanInteractionCall(messages, {
      source: { callId: "call-1" },
      streamingScopeId: "turn-2",
    }),
    false,
  );
  assert.equal(hasMatchingHumanInteractionCall(messages, { source: { callId: "call-1" } }), true);
  assert.equal(hasMatchingHumanInteractionCall(messages, { source: {} }), false);
});

test("matchesHumanInteractionCall rejects the wrong explicit business node", () => {
  const rightNodeCall: AiMessage = {
    ...functionCallMessage("call-1", "turn-1"),
    additionalProperties: { interactionNodeId: "root/right", nodeName: "Left" },
  };
  const target = {
    source: { callId: "call-1", nodeId: "root/left", providerScopeId: "sdk-request-port" },
    streamingScopeId: "turn-1",
  };

  assert.equal(matchesHumanInteractionCall(rightNodeCall, target), false);
  assert.equal(hasMatchingHumanInteractionCall([rightNodeCall], target), false);
  assert.equal(
    matchesHumanInteractionCall(
      {
        ...rightNodeCall,
        additionalProperties: { interactionNodeId: "root/left", nodeName: "Right" },
      },
      target,
    ),
    true,
  );
});

test("matchesHumanInteractionCall preserves matching when node attribution is unavailable", () => {
  const call = functionCallMessage("call-1", "turn-1");
  assert.equal(
    matchesHumanInteractionCall(call, {
      source: { callId: "call-1", nodeId: "root/left" },
      streamingScopeId: "turn-1",
    }),
    true,
  );
  assert.equal(
    matchesHumanInteractionCall(
      {
        ...call,
        additionalProperties: { interactionNodeId: "root/right" },
      },
      { source: { callId: "call-1" }, streamingScopeId: "turn-1" },
    ),
    true,
  );
});
