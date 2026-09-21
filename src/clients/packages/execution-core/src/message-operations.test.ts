import assert from "node:assert/strict";
import test from "node:test";
import { mergeStreamingMessages } from "./message";
import type { ExecutionMessage } from "./types";

function message(text: string, operation = "AppendText", blockId = "text"): ExecutionMessage {
  return {
    messageId: "canonical",
    role: "assistant",
    author: "test",
    contents: [{ type: "TextContent", content: text, additionalProperties: { blockId } }],
    additionalProperties: {
      messageOperation: operation,
    },
  };
}

test("deltas preserve repeated text and spaces without revision metadata", () => {
  const first = message("a");
  const projected = mergeStreamingMessages([], [first, message("a"), message(" "), message("b")]);
  assert.equal(projected[0].contents[0].content, "aa b");
  assert.equal(first.contents[0].content, "a");
  const replaced = mergeStreamingMessages(projected, [message("short", "PutMessage")]);
  assert.equal(replaced.length, 1);
  assert.equal(replaced[0].contents[0].content, "short");
});

test("same-type blocks retain identities and interleaved updates find the correct block", () => {
  const projected = mergeStreamingMessages(
    [],
    [message("a"), message("other", "AppendText", "other"), message("b")],
  );
  assert.deepEqual(
    projected[0].contents.map((content) => content.content),
    ["ab", "other"],
  );
});

test("final callback replaces accumulated content and sealing preserves it", () => {
  const projected = mergeStreamingMessages([], [message("a"), message("b")]);
  const complete = message("ab", "PutMessage");
  const sealed = { ...message("", "SealMessage"), contents: [] };
  const result = mergeStreamingMessages(projected, [complete, complete, sealed]);
  assert.equal(result.length, 1);
  assert.equal(result[0].contents[0].content, "ab");
});

test("history and realtime share canonical identity regardless of local turn scope", () => {
  const history = { ...message("stored", "PutMessage"), streamingScopeId: "history" };
  const live = { ...message(" tail"), streamingScopeId: "live" };
  const result = mergeStreamingMessages([history], [live]);
  assert.equal(result.length, 1);
  assert.equal(result[0].contents[0].content, "stored tail");
});

for (const [type, uri] of [
  ["UriContent", "https://example.test/image.png"],
  ["DataContent", "data:image/png;base64,AQID"],
]) {
  test(`${type} preserves its payload and metadata through block updates and sealing`, () => {
    const media = {
      type,
      uri,
      mediaType: "image/png",
      additionalProperties: { blockId: "image", detail: "high" },
    };
    const incoming = { ...message("", "PutBlock"), contents: [media] };
    const projected = mergeStreamingMessages([], [incoming, incoming]);
    const sealed = mergeStreamingMessages(projected, [
      { ...message("", "SealMessage"), contents: [] },
    ]);

    assert.equal(sealed.length, 1);
    assert.deepEqual(sealed[0].contents, [media]);
    assert.notEqual(sealed[0].contents[0], media);
  });
}
