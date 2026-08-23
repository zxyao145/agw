import assert from "node:assert/strict";
import test from "node:test";

import { buildSettingsHref, getDesktopChatReturnHref } from "./app-shell-routing.ts";

test("Desktop Settings preserves the selected Chat project and conversation", () => {
  const chatHref = "/desktop/chat/?projectId=project-2&conversationId=conversation-3";

  assert.equal(
    buildSettingsHref("/dashboard/", chatHref),
    "/dashboard/?returnTo=%2Fdesktop%2Fchat%2F%3FprojectId%3Dproject-2%26conversationId%3Dconversation-3",
  );
  assert.equal(
    buildSettingsHref("/settings/#appearance", chatHref),
    "/settings/?returnTo=%2Fdesktop%2Fchat%2F%3FprojectId%3Dproject-2%26conversationId%3Dconversation-3#appearance",
  );
});

test("Desktop Settings accepts only the dedicated Chat route as a return target", () => {
  assert.equal(
    getDesktopChatReturnHref("/desktop/chat/?projectId=project-2&conversationId=conversation-3"),
    "/desktop/chat/?projectId=project-2&conversationId=conversation-3",
  );
  assert.equal(getDesktopChatReturnHref("https://example.com/desktop/chat/"), "/desktop/chat/");
  assert.equal(getDesktopChatReturnHref("/projects/"), "/desktop/chat/");
  assert.equal(getDesktopChatReturnHref(null), "/desktop/chat/");
});
