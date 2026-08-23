import assert from "node:assert/strict";
import test from "node:test";

import {
  getChatRouteSessionAction,
  getConversationHydrationKey,
  getRouteHydrationKey,
} from "./session-routing.ts";

test("route action selects a project when there is no conversation route", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    hydratedRouteKey: null,
  });

  assert.deepEqual(action, { type: "selectProject", projectId });
});

test("route action hydrates conversation routes", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";
  const conversationId = "conversation-123";

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    queryConversationId: conversationId,
    hydratedRouteKey: null,
  });

  assert.deepEqual(action, {
    type: "hydrateConversation",
    hydrateKey: `${projectId}:conversation:${conversationId}`,
    projectId,
    conversationId,
  });
});

test("route action ignores the conversation route created by the active local session", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";
  const conversationId = "conversation-123";
  const hydratedRouteKey = getConversationHydrationKey(projectId, conversationId);

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    queryConversationId: conversationId,
    hydratedRouteKey,
  });

  assert.deepEqual(action, { type: "ignore" });
});

test("route action clears only local chat state when no project is in the route", () => {
  const action = getChatRouteSessionAction({
    queryProjectId: null,
    hydratedRouteKey: null,
  });

  assert.deepEqual(action, { type: "clearLocal" });
});

test("route hydration key is available only for hydrate actions", () => {
  assert.equal(
    getRouteHydrationKey({
      type: "hydrateConversation",
      hydrateKey: "project-1:conversation:conversation-1",
      projectId: "project-1",
      conversationId: "conversation-1",
    }),
    "project-1:conversation:conversation-1",
  );

  assert.equal(getRouteHydrationKey({ type: "ignore" }), null);
});
