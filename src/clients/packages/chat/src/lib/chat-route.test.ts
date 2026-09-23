import assert from "node:assert/strict";
import test from "node:test";

import { buildChatHref } from "./chat-route.ts";

test("builds chat links with project and conversation", () => {
  assert.equal(
    buildChatHref("/chat", { projectId: "project-1", conversationId: "conversation-1" }),
    "/chat/?projectId=project-1&conversationId=conversation-1",
  );
  assert.equal(
    buildChatHref("/desktop/chat", { projectId: "project-1", conversationId: null }),
    "/desktop/chat/?projectId=project-1",
  );
  assert.equal(
    buildChatHref("/desktop/chat", { projectId: null, conversationId: "ignored-conversation" }),
    "/desktop/chat/",
  );
});
