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
      callId: "call-1",
      streamingScopeId: "turn-2",
    }),
    true,
  );
  assert.equal(
    matchesHumanInteractionCall(currentCall, {
      callId: "call-1",
      streamingScopeId: "turn-1",
    }),
    false,
  );
});

test("hasMatchingHumanInteractionCall does not bind a repeated call id to an older turn", () => {
  const messages = [functionCallMessage("call-1", "turn-1")];

  assert.equal(
    hasMatchingHumanInteractionCall(messages, {
      callId: "call-1",
      streamingScopeId: "turn-2",
    }),
    false,
  );
  assert.equal(hasMatchingHumanInteractionCall(messages, { callId: "call-1" }), true);
  assert.equal(hasMatchingHumanInteractionCall(messages, {}), false);
});
