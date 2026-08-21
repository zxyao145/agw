import assert from "node:assert/strict";
import test from "node:test";

import { createUserMessage, validateImageAttachments, type ChatImageAttachment } from "./index";

test("image attachment validation and user messages share the platform-neutral contract", () => {
  const attachment: ChatImageAttachment = {
    id: "image-1",
    name: "screen.png",
    mediaType: "image/png",
    size: 4,
    dataUrl: "data:image/png;base64,AQID",
  };

  assert.equal(
    validateImageAttachments([{ name: "next.jpg", size: 8, type: "image/jpeg" }], [attachment]),
    null,
  );
  assert.deepEqual(createUserMessage("describe this", [attachment]).contents, [
    { type: "DataContent", uri: attachment.dataUrl, name: attachment.name },
    { type: "TextContent", content: "describe this" },
  ]);
});
