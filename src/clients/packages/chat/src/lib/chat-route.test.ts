import assert from "node:assert/strict";
import test from "node:test";

// @ts-expect-error Node's type stripping requires the explicit TypeScript extension.
import { buildChatHref, getChatRouteRedirect } from "./chat-route.ts";

test("builds Web and Desktop chat links with project and context", () => {
  assert.equal(
    buildChatHref("/chat", { projectId: "project-1", contextId: "context-1" }),
    "/chat/?projectId=project-1&contextId=context-1",
  );
  assert.equal(
    buildChatHref("/desktop/chat", { projectId: "project-1", contextId: null }),
    "/desktop/chat/?projectId=project-1",
  );
  assert.equal(
    buildChatHref("/desktop/chat", { projectId: null, contextId: "ignored-context" }),
    "/desktop/chat/",
  );
});

test("redirects only across a mismatched Chat runtime boundary", () => {
  assert.equal(
    getChatRouteRedirect({
      isDesktop: true,
      pathname: "/chat/",
      search: "?projectId=project-1&contextId=context-1",
    }),
    "/desktop/chat/?projectId=project-1&contextId=context-1",
  );
  assert.equal(
    getChatRouteRedirect({
      isDesktop: false,
      pathname: "/desktop/chat/",
      search: "?projectId=project-1",
    }),
    "/chat/?projectId=project-1",
  );
  assert.equal(
    getChatRouteRedirect({ isDesktop: true, pathname: "/desktop/chat/", search: "" }),
    null,
  );
  assert.equal(getChatRouteRedirect({ isDesktop: false, pathname: "/chat/", search: "" }), null);
});

test("route redirects normalize search strings without dropping parameters", () => {
  assert.equal(
    getChatRouteRedirect({
      isDesktop: true,
      pathname: "/chat",
      search: "projectId=project%202&custom=value",
    }),
    "/desktop/chat/?projectId=project%202&custom=value",
  );
});
