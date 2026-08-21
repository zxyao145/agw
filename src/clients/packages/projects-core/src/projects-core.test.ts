import assert from "node:assert/strict";
import test from "node:test";

import type { AgwApiClient } from "@agw/api";
import { createProjectContextService, createProjectFilesService } from "./index";

test("project core services use the injected API client", async () => {
  const requests: string[] = [];
  const client = {
    apiGet: async (path: string) => {
      requests.push(path);
      if (path === "/api/files/list") return { items: [] };
      return [
        {
          projectId: "project-1",
          contextId: "context-1",
          title: "Mobile",
          executionCount: 0,
          messageCount: 0,
          createTime: "2026-08-21T00:00:00Z",
        },
      ];
    },
    apiPost: async () => undefined,
    apiPut: async () => undefined,
    apiDelete: async () => undefined,
  } as unknown as AgwApiClient;

  const contexts = await createProjectContextService(client).getProjectContexts("project-1");
  const files = await createProjectFilesService(client).listFiles("project-1", "");

  assert.equal(contexts[0]?.contextId, "context-1");
  assert.deepEqual(files, { items: [] });
  assert.deepEqual(requests, ["/api/projects/{projectId}/contexts", "/api/files/list"]);
});
