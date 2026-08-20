import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { groupContentsByType } from "./message";

test("data contents remain separate message nodes with names", () => {
  const message: AiMessage = {
    messageId: "message-1",
    role: "user",
    contents: [
      { type: "DataContent", uri: "data:image/png;base64,AQ==", name: "one.png" },
      { type: "DataContent", uri: "data:image/webp;base64,Ag==", name: "two.webp" },
      { type: "TextContent", content: "describe these" },
    ],
  };

  assert.deepEqual(groupContentsByType(message), [
    { type: "DataContent", content: "data:image/png;base64,AQ==", name: "one.png" },
    { type: "DataContent", content: "data:image/webp;base64,Ag==", name: "two.webp" },
    { type: "TextContent", content: "describe these" },
  ]);
});
