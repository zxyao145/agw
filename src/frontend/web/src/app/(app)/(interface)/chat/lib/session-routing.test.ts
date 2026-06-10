import assert from "node:assert/strict";
import test from "node:test";

import {
  getChatRouteSessionAction,
  getContextHydrationKey,
  getTaskHydrationKey,
} from "./session-routing";

test("route action ignores the task route created by the active local session", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";
  const taskId = "22222222-2222-2222-2222-222222222222";
  const hydratedTaskKey = getTaskHydrationKey(projectId, taskId);

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    queryTaskId: taskId,
    hydratedTaskKey,
  });

  assert.deepEqual(action, { type: "ignore" });
});

test("route action hydrates an explicit task route that is not already local", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";
  const taskId = "22222222-2222-2222-2222-222222222222";

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    queryTaskId: taskId,
    hydratedTaskKey: null,
  });

  assert.deepEqual(action, {
    type: "hydrate",
    hydrateKey: `${projectId}:task:${taskId}`,
    projectId,
    taskId,
  });
});

test("route action hydrates context route before task route", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";
  const taskId = "22222222-2222-2222-2222-222222222222";
  const contextId = "ctx-123";

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    queryTaskId: taskId,
    queryContextId: contextId,
    hydratedTaskKey: null,
  });

  assert.deepEqual(action, {
    type: "hydrateContext",
    hydrateKey: `${projectId}:context:${contextId}`,
    projectId,
    contextId,
    taskId,
  });
});

test("route action ignores the context route created by the active local session", () => {
  const projectId = "11111111-1111-1111-1111-111111111111";
  const contextId = "ctx-123";
  const hydratedTaskKey = getContextHydrationKey(projectId, contextId);

  const action = getChatRouteSessionAction({
    queryProjectId: projectId,
    queryTaskId: null,
    queryContextId: contextId,
    hydratedTaskKey,
  });

  assert.deepEqual(action, { type: "ignore" });
});

test("route action clears only local chat state when no project is in the route", () => {
  const action = getChatRouteSessionAction({
    queryProjectId: null,
    queryTaskId: null,
    hydratedTaskKey: null,
  });

  assert.deepEqual(action, { type: "clearLocal" });
});
