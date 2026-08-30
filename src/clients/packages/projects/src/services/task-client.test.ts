import assert from "node:assert/strict";
import test from "node:test";

import { clearAntiforgeryToken } from "@agw/api";
import {
  clearProjectConversationRecords,
  createProjectConversation,
  deleteAllProjectConversations,
  deleteProjectConversation,
  getProjectConversationDetails,
  getProjectConversationMessages,
  getProjectConversations,
  updateProjectConversationTitle,
} from "./task-client";

test.beforeEach(() => clearAntiforgeryToken());
test.afterEach(() => clearAntiforgeryToken());

function createAntiforgeryResponse(): Response {
  return Response.json({
    code: 200,
    title: "OK",
    data: { requestToken: "csrf-projects" },
  });
}

test("clearProjectContextRecords deletes records for a project context", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    if (String(input) === "/api/auth/antiforgery") return createAntiforgeryResponse();
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await clearProjectConversationRecords(
    "project-1",
    "11111111-1111-1111-1111-000000000010",
  );

  assert.equal(result, true);
  assert.deepEqual(requests, [
    {
      url: "/api/auth/antiforgery",
      init: { credentials: "same-origin" },
    },
    {
      url: "/api/projects/project-1/conversations/11111111-1111-1111-1111-000000000010/clear-records",
      init: {
        method: "DELETE",
        headers: { "X-CSRF-TOKEN": "csrf-projects" },
        signal: undefined,
        credentials: "same-origin",
      },
    },
  ]);
});

test("clearProjectContextRecords returns false when the context is not found", async (t) => {
  const originalFetch = globalThis.fetch;

  globalThis.fetch = (async (input: RequestInfo | URL) =>
    String(input) === "/api/auth/antiforgery"
      ? createAntiforgeryResponse()
      : new Response(null, { status: 404 })) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await clearProjectConversationRecords(
    "project-1",
    "11111111-1111-1111-1111-000000000011",
  );

  assert.equal(result, false);
});

test("clearProjectContextRecords returns false when ids are blank", async (t) => {
  const originalFetch = globalThis.fetch;
  let fetchCalled = false;

  globalThis.fetch = (async () => {
    fetchCalled = true;
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await clearProjectConversationRecords("project-1", "");

  assert.equal(result, false);
  assert.equal(fetchCalled, false);
});

test("deleteAllProjectContexts deletes all project contexts", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    if (String(input) === "/api/auth/antiforgery") return createAntiforgeryResponse();
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await deleteAllProjectConversations("project-1");

  assert.equal(result, true);
  assert.deepEqual(requests, [
    {
      url: "/api/auth/antiforgery",
      init: { credentials: "same-origin" },
    },
    {
      url: "/api/projects/project-1/conversations",
      init: {
        method: "DELETE",
        headers: { "X-CSRF-TOKEN": "csrf-projects" },
        signal: undefined,
        credentials: "same-origin",
      },
    },
  ]);
});

test("deleteAllProjectContexts returns false and skips fetch when project id is blank", async (t) => {
  const originalFetch = globalThis.fetch;
  let fetchCalled = false;

  globalThis.fetch = (async () => {
    fetchCalled = true;
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await deleteAllProjectConversations("");

  assert.equal(result, false);
  assert.equal(fetchCalled, false);
});

test("getProjectConversations gets the complete conversation list for a project", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return Response.json({
      code: 200,
      title: "OK",
      data: [
        {
          projectId: "project-1",
          conversationId: "11111111-1111-1111-1111-000000000001",
          contextId: "context-empty",
          jobId: null,
          title: "New Chat",
          latestStatus: 1,
          executionCount: 1,
          messageCount: 0,
          createTime: "2026-01-01T00:00:00Z",
          updateTime: "2026-01-02T00:00:00Z",
          errorMessage: null,
        },
        {
          projectId: "project-1",
          conversationId: "11111111-1111-1111-1111-000000000002",
          contextId: "context-1",
          jobId: "job-1",
          title: "Tokyo trip",
          latestStatus: 2,
          executionCount: 2,
          messageCount: 4,
          createTime: "2026-01-01T00:00:00Z",
          updateTime: "2026-01-02T00:00:00Z",
          errorMessage: null,
        },
        {
          projectId: "project-1",
          conversationId: "11111111-1111-1111-1111-000000000003",
          contextId: "context-cleared",
          jobId: null,
          title: "Cleared chat",
          latestStatus: null,
          executionCount: 0,
          messageCount: 0,
          createTime: "2026-01-01T00:00:00Z",
          updateTime: "2026-01-03T00:00:00Z",
          errorMessage: null,
        },
      ],
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await getProjectConversations("project-1");

  assert.equal(result.length, 3);
  assert.equal(result[0].conversationId, "11111111-1111-1111-1111-000000000001");
  assert.equal(result[0].contextId, "context-empty");
  assert.equal(result[0].executionCount, 1);
  assert.equal(result[0].messageCount, 0);
  assert.equal(result[1].conversationId, "11111111-1111-1111-1111-000000000002");
  assert.equal(result[1].contextId, "context-1");
  assert.equal(result[1].jobId, "job-1");
  assert.equal(result[1].executionCount, 2);
  assert.equal(result[2].conversationId, "11111111-1111-1111-1111-000000000003");
  assert.equal(result[2].contextId, "context-cleared");
  assert.equal(result[2].executionCount, 0);
  assert.equal(result[2].messageCount, 0);
  assert.deepEqual(requests, [
    {
      url: "/api/projects/project-1/conversations",
      init: {
        method: "GET",
        headers: {},
        signal: undefined,
        credentials: "same-origin",
      },
    },
  ]);
});

test("createProjectConversation persists a blank conversation immediately", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    if (String(input) === "/api/auth/antiforgery") return createAntiforgeryResponse();
    return Response.json({
      code: 200,
      title: "OK",
      data: {
        projectId: "project-1",
        conversationId: "11111111-1111-1111-1111-000000000003",
        contextId: "context-new",
        title: "New Chat",
        executionCount: 0,
        messageCount: 0,
        createTime: "2026-08-21T00:00:00Z",
        updateTime: "2026-08-21T00:00:00Z",
        errorMessage: null,
      },
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await createProjectConversation("project-1");

  assert.equal(result.contextId, "context-new");
  assert.deepEqual(requests, [
    {
      url: "/api/auth/antiforgery",
      init: { credentials: "same-origin" },
    },
    {
      url: "/api/projects/project-1/conversations",
      init: {
        method: "POST",
        headers: {
          "content-type": "application/json",
          "X-CSRF-TOKEN": "csrf-projects",
        },
        body: '{"contextId":null}',
        signal: undefined,
        credentials: "same-origin",
      },
    },
  ]);
});

test("getProjectConversationDetails encodes conversation id path parameter", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return Response.json({
      code: 200,
      title: "OK",
      data: {
        projectId: "project-1",
        conversationId: "11111111-1111-1111-1111-000000000004",
        contextId: "context/a b",
        jobId: "job-1",
        title: "Tokyo trip",
        latestStatus: 2,
        executionCount: 2,
        messageCount: 1,
        createTime: "2026-01-01T00:00:00Z",
        updateTime: "2026-01-02T00:00:00Z",
        errorMessage: null,
        usage: null,
        resumeState: null,
      },
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await getProjectConversationDetails(
    "project-1",
    "11111111-1111-1111-1111-000000000004",
  );

  assert.equal(result.conversationId, "11111111-1111-1111-1111-000000000004");
  assert.equal(result.contextId, "context/a b");
  assert.equal(result.jobId, "job-1");
  assert.equal(result.executionCount, 2);
  assert.deepEqual(requests, [
    {
      url: "/api/projects/project-1/conversations/11111111-1111-1111-1111-000000000004",
      init: {
        method: "GET",
        headers: {},
        signal: undefined,
        credentials: "same-origin",
      },
    },
  ]);
});

test("getProjectConversationMessages sends the directional cursor page query", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return Response.json({
      code: 200,
      title: "OK",
      data: {
        items: [],
        nextCursor: "next/cursor",
        hasMore: true,
      },
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await getProjectConversationMessages(
    "project-1",
    "11111111-1111-1111-1111-000000000012",
    {
      direction: "older",
      cursor: "before/cursor",
      pageSize: 25,
    },
  );

  assert.deepEqual(result, { items: [], nextCursor: "next/cursor", hasMore: true });
  assert.equal(
    requests[0]?.url,
    "/api/projects/project-1/conversations/11111111-1111-1111-1111-000000000012/messages?direction=older&cursor=before%2Fcursor&pageSize=25",
  );
});

test("updateProjectContextTitle puts the title update for a project context", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    if (String(input) === "/api/auth/antiforgery") return createAntiforgeryResponse();
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await updateProjectConversationTitle(
    "project-1",
    "11111111-1111-1111-1111-000000000013",
    "Renamed conversation",
  );

  assert.equal(result, true);
  assert.deepEqual(requests, [
    {
      url: "/api/auth/antiforgery",
      init: { credentials: "same-origin" },
    },
    {
      url: "/api/projects/project-1/conversations/11111111-1111-1111-1111-000000000013/title",
      init: {
        method: "PUT",
        headers: {
          "X-CSRF-TOKEN": "csrf-projects",
          "content-type": "application/json",
        },
        signal: undefined,
        credentials: "same-origin",
        body: JSON.stringify({ title: "Renamed conversation" }),
      },
    },
  ]);
});

test("updateProjectContextTitle returns false and skips fetch when ids or title are blank", async (t) => {
  const originalFetch = globalThis.fetch;
  let fetchCalled = false;

  globalThis.fetch = (async () => {
    fetchCalled = true;
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await updateProjectConversationTitle(
    "project-1",
    "11111111-1111-1111-1111-000000000014",
    "   ",
  );

  assert.equal(result, false);
  assert.equal(fetchCalled, false);
});

test("deleteProjectContext deletes a project context", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    if (String(input) === "/api/auth/antiforgery") return createAntiforgeryResponse();
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await deleteProjectConversation(
    "project-1",
    "11111111-1111-1111-1111-000000000015",
  );

  assert.equal(result, true);
  assert.deepEqual(requests, [
    {
      url: "/api/auth/antiforgery",
      init: { credentials: "same-origin" },
    },
    {
      url: "/api/projects/project-1/conversations/11111111-1111-1111-1111-000000000015",
      init: {
        method: "DELETE",
        headers: { "X-CSRF-TOKEN": "csrf-projects" },
        signal: undefined,
        credentials: "same-origin",
      },
    },
  ]);
});

test("deleteProjectContext returns false when ids are blank", async (t) => {
  const originalFetch = globalThis.fetch;
  let fetchCalled = false;

  globalThis.fetch = (async () => {
    fetchCalled = true;
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await deleteProjectConversation("project-1", "");

  assert.equal(result, false);
  assert.equal(fetchCalled, false);
});
