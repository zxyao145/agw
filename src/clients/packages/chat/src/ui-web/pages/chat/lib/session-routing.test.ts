import assert from "node:assert/strict";
import test from "node:test";

import {
  getChatRouteSessionAction,
  getContextHydrationKey,
  getRouteHydrationKey,
} from "./session-routing.ts";

test("route action selects a project when there is no context route", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    hydratedRouteKey: null,
  });

  assert.deepEqual(action, { type: "selectProject", projectId });
});

test("route action hydrates context routes", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";
  const contextId = "ctx-123";

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    queryContextId: contextId,
    hydratedRouteKey: null,
  });

  assert.deepEqual(action, {
    type: "hydrateContext",
    hydrateKey: `${projectId}:context:${contextId}`,
    projectId,
    contextId,
  });
});

test("route action ignores the context route created by the active local session", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";
  const contextId = "ctx-123";
  const hydratedRouteKey = getContextHydrationKey(projectId, contextId);

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    queryContextId: contextId,
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
      type: "hydrateContext",
      hydrateKey: "project-1:context:context-1",
      projectId: "project-1",
      contextId: "context-1",
    }),
    "project-1:context:context-1",
  );

  assert.equal(getRouteHydrationKey({ type: "ignore" }), null);
});
