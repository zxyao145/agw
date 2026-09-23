import assert from "node:assert/strict";
import test from "node:test";
import { mergeStreamingMessages } from "./message";
import type { ExecutionMessage } from "./types";

function message(text: string, blockId = "text"): ExecutionMessage {
  return {
    messageId: "canonical",
    role: "assistant",
    author: "test",
    contents: [{ type: "TextContent", content: text, additionalProperties: { blockId } }],
    additionalProperties: {
      producerScopeId: "producer",
    },
  };
}

test("deltas preserve repeated text and spaces", () => {
  const first = message("a");
  const projected = mergeStreamingMessages([], [first, message("a"), message(" "), message("b")]);
  assert.equal(projected[0].contents[0].content, "aa b");
  assert.equal(first.contents[0].content, "a");
});

test("same-type blocks retain identities and interleaved updates find the correct block", () => {
  const projected = mergeStreamingMessages(
    [],
    [message("a"), message("other", "other"), message("b")],
  );
  assert.deepEqual(
    projected[0].contents.map((content) => content.content),
    ["ab", "other"],
  );
});

test("empty updates preserve accumulated content and later updates append", () => {
  const projected = mergeStreamingMessages([], [message("a"), message("b")]);
  const empty = { ...message(""), contents: [] };
  const result = mergeStreamingMessages(projected, [empty, message("c")]);
  assert.equal(result.length, 1);
  assert.equal(result[0].contents[0].content, "abc");
});

test("history and realtime share canonical identity regardless of local turn scope", () => {
  const history = { ...message("stored"), streamingScopeId: "history" };
  const live = { ...message(" tail"), streamingScopeId: "live" };
  const result = mergeStreamingMessages([history], [live]);
  assert.equal(result.length, 1);
  assert.equal(result[0].contents[0].content, "stored tail");
});

for (const [type, uri] of [
  ["UriContent", "https://example.test/image.png"],
  ["DataContent", "data:image/png;base64,AQID"],
]) {
  test(`${type} appends every payload with its metadata`, () => {
    const media = {
      type,
      uri,
      mediaType: "image/png",
      additionalProperties: { blockId: "image", detail: "high" },
    };
    const incoming = { ...message(""), contents: [media] };
    const projected = mergeStreamingMessages([], [incoming, incoming]);
    assert.equal(projected.length, 1);
    assert.deepEqual(projected[0].contents, [media, media]);
    assert.notEqual(projected[0].contents[0], media);
  });
}
