import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import type { ConversationRenderItem, PresentedContent, PresentedMessage } from "@agw/chat-core";

import {
  buildUserInputAnchors,
  getActiveUserInputMarkerKey,
  layoutUserInputMarkers,
  truncateUserInputPreview,
  type UserInputAnchor,
} from "./user-input-navigation";

function messageItem(
  key: string,
  role: AiMessage["role"],
  contents: PresentedContent[],
): ConversationRenderItem {
  const alignment = role === "user" ? "right" : "left";
  const message: PresentedMessage = {
    source: { messageId: key, role, contents: [] },
    identity: key,
    alignment,
    width: "normal",
    meta: null,
    contents,
  };
  return { type: "message", key, alignment, width: "normal", message };
}

test("user input anchors keep visible user rows and normalize their previews", () => {
  const items: ConversationRenderItem[] = [
    messageItem("user-text", "user", [
      { type: "markdown", markdown: "  First line\n\nsecond   line  ", sourceType: "TextContent" },
      { type: "image", uri: "data:image/png;base64,a", name: "ignored.png" },
    ]),
    messageItem("assistant", "assistant", [
      { type: "markdown", markdown: "Assistant reply", sourceType: "TextContent" },
    ]),
    messageItem("user-image", "user", [
      { type: "image", uri: "data:image/png;base64,b", name: "diagram.png" },
      { type: "image", uri: "data:image/png;base64,c", name: "details.png" },
    ]),
    messageItem("user-unnamed-image", "user", [
      { type: "image", uri: "data:image/png;base64,d", name: null },
    ]),
    messageItem("user-empty", "user", []),
  ];

  assert.deepEqual(buildUserInputAnchors(items), [
    { key: "user-text", itemIndex: 0, preview: "First line second line" },
    { key: "user-image", itemIndex: 2, preview: "diagram.png, details.png" },
    { key: "user-unnamed-image", itemIndex: 3, preview: "Image input" },
    { key: "user-empty", itemIndex: 4, preview: "User input" },
  ]);
});

test("user input previews truncate by Unicode code point", () => {
  assert.equal(truncateUserInputPreview("  a\n b  ", 4), "a b");
  assert.equal(truncateUserInputPreview("😀😀😀", 2), "😀…");
  assert.equal(truncateUserInputPreview("abcdef", 4), "abc…");
});

test("user input marker layout includes the history-loader row offset", () => {
  const anchors: UserInputAnchor[] = [
    { key: "first", itemIndex: 0, preview: "First" },
    { key: "second", itemIndex: 2, preview: "Second" },
  ];
  const markers = layoutUserInputMarkers(
    anchors,
    [{ start: 0 }, { start: 72 }, { start: 172 }, { start: 472 }],
    1,
  );

  assert.deepEqual(
    markers.map(({ key, rowIndex, start }) => ({ key, rowIndex, start })),
    [
      { key: "first", rowIndex: 1, start: 72 },
      { key: "second", rowIndex: 3, start: 472 },
    ],
  );
  assert.equal(getActiveUserInputMarkerKey(markers, 0), "first");
  assert.equal(getActiveUserInputMarkerKey(markers, 50), "first");
  assert.equal(getActiveUserInputMarkerKey(markers, 440), "second");
  assert.equal(getActiveUserInputMarkerKey(markers, 0, true), "second");
});

test("marker layout skips anchors whose virtual row is unavailable", () => {
  const markers = layoutUserInputMarkers(
    [{ key: "missing", itemIndex: 4, preview: "Missing" }],
    [{ start: 0 }],
    0,
  );

  assert.deepEqual(markers, []);
  assert.equal(getActiveUserInputMarkerKey(markers, 0), null);
});
