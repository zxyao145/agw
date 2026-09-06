import assert from "node:assert/strict";
import test from "node:test";

import type { AgwApiClient } from "@agw/api";
import { createProjectConversationService, createProjectFilesService } from "./index";

test("project core services use the injected API client", async () => {
  const requests: string[] = [];
  const client = {
    apiGet: async (path: string) => {
      requests.push(path);
      if (path === "/api/files/list") return { items: [] };
      return {
        total: 2,
        pageIndex: 1,
        pageSize: 20,
        items: [
          {
            projectId: "project-1",
            conversationId: "11111111-1111-1111-1111-000000000001",
            contextId: "context-1",
            title: "Mobile",
            executionCount: 0,
            messageCount: 0,
            createTime: "2026-08-21T00:00:00Z",
          },
          {
            projectId: "project-1",
            conversationId: "11111111-1111-1111-1111-000000000002",
            contextId: "external-agent-context",
            jobId: null,
            title: "Claude Code",
            executionCount: 1,
            messageCount: 0,
            createTime: "2026-08-21T00:01:00Z",
          },
        ],
      };
    },
    apiPost: async () => undefined,
    apiPut: async () => undefined,
    apiDelete: async () => undefined,
  } as unknown as AgwApiClient;

  const conversations =
    await createProjectConversationService(client).getProjectConversations("project-1");
  const files = await createProjectFilesService(client).listFiles("project-1", "");

  assert.equal(conversations.items[0]?.conversationId, "11111111-1111-1111-1111-000000000001");
  assert.equal(conversations.items[0]?.contextId, "context-1");
  assert.equal(conversations.items[1]?.conversationId, "11111111-1111-1111-1111-000000000002");
  assert.equal(conversations.items[1]?.contextId, "external-agent-context");
  assert.deepEqual(files, { items: [] });
  assert.deepEqual(requests, ["/api/projects/{projectId}/conversations", "/api/files/list"]);
});

test("project conversation pages forward filters and cancellation", async () => {
  let requestOptions: unknown;
  const client = {
    apiGet: async (_path: string, options?: unknown) => {
      requestOptions = options;
      return { items: [], total: 0, pageIndex: 2, pageSize: 10 };
    },
    apiPost: async () => undefined,
    apiPut: async () => undefined,
    apiDelete: async () => undefined,
  } as unknown as AgwApiClient;
  const controller = new AbortController();

  const page = await createProjectConversationService(client).getProjectConversations("project-1", {
    pageIndex: 2,
    pageSize: 10,
    contextId: "context-1",
    signal: controller.signal,
  });

  assert.deepEqual(page, { items: [], total: 0, pageIndex: 2, pageSize: 10 });
  assert.deepEqual(requestOptions, {
    params: {
      path: { projectId: "project-1" },
      query: { pageIndex: 2, pageSize: 10, contextId: "context-1" },
    },
    signal: controller.signal,
  });
});

test("project conversation history aggregates every newer message page", async () => {
  const cursors: Array<string | undefined> = [];
  const client = {
    apiGet: async (path: string, options?: { params?: { query?: { cursor?: string } } }) => {
      if (path.endsWith("/messages")) {
        const cursor = options?.params?.query?.cursor;
        cursors.push(cursor);
        return cursor
          ? {
              items: [{ messageId: "message-2", role: "assistant", contents: [] }],
              nextCursor: null,
              hasMore: false,
            }
          : {
              items: [{ messageId: "message-1", role: "user", contents: [] }],
              nextCursor: "cursor-1",
              hasMore: true,
            };
      }

      return {
        projectId: "project-1",
        conversationId: "11111111-1111-1111-1111-000000000001",
        contextId: "context-1",
        title: "History",
        executionCount: 1,
        messageCount: 2,
        createTime: "2026-08-21T00:00:00Z",
        usage: null,
        resumeState: null,
      };
    },
    apiPut: async () => undefined,
    apiDelete: async () => undefined,
  } as unknown as AgwApiClient;

  const history = await createProjectConversationService(client).getProjectConversationHistory(
    "project-1",
    "11111111-1111-1111-1111-000000000001",
  );

  assert.deepEqual(
    history.messages.map((message) => message.messageId),
    ["message-1", "message-2"],
  );
  assert.deepEqual(cursors, [undefined, "cursor-1"]);
});
