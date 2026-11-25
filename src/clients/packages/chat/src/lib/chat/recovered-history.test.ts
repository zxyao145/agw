import assert from "node:assert/strict";
import test from "node:test";
import type { AiMessage } from "@agw/api";
import { liveMessageScope, mergeRecoveredHistory } from "./recovered-history";

function message(
  role: string,
  messageId: string,
  content: string,
  streamingScopeId = "first",
): AiMessage {
  return { role, messageId, streamingScopeId, contents: [{ type: "TextContent", content }] };
}

test("server history restores the user's input and all messages beyond the detached replay limit", () => {
  const input = message("user", "input", "first question");
  const answers = Array.from({ length: 220 }, (_, n) => message("assistant", String(n), String(n)));
  const tail = answers.slice(-200);
  const replay = {
    ...tail[0],
    streamingScopeId: undefined,
    additionalProperties: { turnId: "first" },
  };
  tail[0] = message(
    "assistant",
    tail[0].messageId!,
    "partial replay",
    liveMessageScope(replay, null),
  );
  assert.deepEqual(mergeRecoveredHistory([input, ...answers], tail, new Set()), [
    input,
    ...answers,
  ]);
});

test("a complete live turn retains its streamed suffix and following turn when history arrives", () => {
  const earlier = message("assistant", "previous", "previous turn", "previous");
  const input = message("user", "input", "question");
  const recorded = message("assistant", "answer", "part");
  const reply = message("assistant", "answer", "part two");
  const next = message("user", "second-input", "next question", "second");
  const history = [earlier, input, recorded];
  assert.deepEqual(
    mergeRecoveredHistory(history, [input, reply, next], new Set(["first", "second"])),
    [earlier, input, reply, next],
  );
});
