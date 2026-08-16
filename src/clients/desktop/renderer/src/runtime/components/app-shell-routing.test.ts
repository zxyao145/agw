import assert from "node:assert/strict";
import test from "node:test";

import { buildSettingsHref, getDesktopChatReturnHref } from "./app-shell-routing.ts";

test("Desktop Settings preserves the selected Chat project and context", () => {
  const chatHref = "/desktop/chat/?projectId=project-2&contextId=context-3";

  assert.equal(
    buildSettingsHref("/dashboard/", chatHref),
    "/dashboard/?returnTo=%2Fdesktop%2Fchat%2F%3FprojectId%3Dproject-2%26contextId%3Dcontext-3",
  );
  assert.equal(
    buildSettingsHref("/settings/#appearance", chatHref),
    "/settings/?returnTo=%2Fdesktop%2Fchat%2F%3FprojectId%3Dproject-2%26contextId%3Dcontext-3#appearance",
  );
});

test("Desktop Settings accepts only the dedicated Chat route as a return target", () => {
  assert.equal(
    getDesktopChatReturnHref("/desktop/chat/?projectId=project-2&contextId=context-3"),
    "/desktop/chat/?projectId=project-2&contextId=context-3",
  );
  assert.equal(getDesktopChatReturnHref("https://example.com/desktop/chat/"), "/desktop/chat/");
  assert.equal(getDesktopChatReturnHref("/projects/"), "/desktop/chat/");
  assert.equal(getDesktopChatReturnHref(null), "/desktop/chat/");
});
