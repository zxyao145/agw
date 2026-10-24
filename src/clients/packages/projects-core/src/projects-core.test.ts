import assert from "node:assert/strict";
import test from "node:test";

import { createBearerApiClient, type AgwApiClient } from "@agw/api";
import { startApiServer } from "@agw/test-harness";
import {
  createProjectConversationService,
  createProjectFilesService,
  getConversationTurnInputs,
} from "./index";

test("conversation turn inputs are read through the given client", async () => {
  const server = await startApiServer({
    "GET /api/projects/conversation-turns": {
      items: [
        { inputMessageId: "input-2", inputSummary: "second" },
        { inputMessageId: "input-1", inputSummary: "first" },
      ],
      nextBeforeSequence: null,
      nextBeforeTurnId: null,
      hasMore: false,
    },
  });
  const client = createBearerApiClient({ baseUrl: server.baseUrl, token: "token-1" });

  const inputs = await getConversationTurnInputs("conversation-1", undefined, client);

  assert.deepEqual(inputs, [
    { inputMessageId: "input-1", inputSummary: "first" },
    { inputMessageId: "input-2", inputSummary: "second" },
  ]);
  assert.equal(server.requests.at(-1)?.headers.authorization, "Bearer token-1");
  assert.equal(server.requests.at(-1)?.query.get("conversationId"), "conversation-1");
});

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

test("conversation lists and details carry the creator and the open state", async () => {
  const conversation = {
    projectId: "project-1",
    contextId: "context-1",
    title: "Chat",
    executionCount: 1,
    messageCount: 2,
    createTime: "2026-10-24T00:00:00Z",
  };
  const server = await startApiServer({
    "GET /api/projects/project-1/conversations": {
      total: 3,
      pageIndex: 1,
      pageSize: 20,
      items: [
        {
          ...conversation,
          conversationId: "conversation-1",
          extensionId: "host",
          openToExtensions: true,
        },
        {
          ...conversation,
          conversationId: "conversation-2",
          extensionId: "sample",
          openToExtensions: false,
        },
        // 旧版 Server 不返回这两个字段。An older Server returns neither field.
        { ...conversation, conversationId: "conversation-3" },
      ],
    },
    "GET /api/projects/project-1/conversations/conversation-2": {
      ...conversation,
      conversationId: "conversation-2",
      extensionId: "sample",
      openToExtensions: false,
      resumeState: null,
    },
  });
  const service = createProjectConversationService(
    createBearerApiClient({ baseUrl: server.baseUrl, token: "token-1" }),
  );

  const page = await service.getProjectConversations("project-1");
  const details = await service.getProjectConversationDetails("project-1", "conversation-2");

  assert.deepEqual(
    page.items.map(({ extensionId, openToExtensions }) => ({
      extensionId,
      openToExtensions,
    })),
    [
      { extensionId: "host", openToExtensions: true },
      { extensionId: "sample", openToExtensions: false },
      { extensionId: null, openToExtensions: false },
    ],
  );
  assert.equal(details.extensionId, "sample");
  assert.equal(details.openToExtensions, false);
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
