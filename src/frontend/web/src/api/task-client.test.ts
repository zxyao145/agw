import assert from "node:assert/strict";
import test from "node:test";

import {
  clearProjectContextRecords,
  deleteAllProjectContexts,
  deleteProjectContext,
  getProjectContextDetails,
  getProjectContexts,
  updateProjectContextTitle,
} from "./task-client";

test("clearProjectContextRecords deletes records for a project context", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await clearProjectContextRecords("project-1", "ctx/a b");

  assert.equal(result, true);
  assert.deepEqual(requests, [
    {
      url: "/api/projects/project-1/contexts/ctx%2Fa%20b/clear-records",
      init: {
        method: "DELETE",
        headers: {},
        signal: undefined,
      },
    },
  ]);
});

test("clearProjectContextRecords returns false when the context is not found", async (t) => {
  const originalFetch = globalThis.fetch;

  globalThis.fetch = (async () => new Response(null, { status: 404 })) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await clearProjectContextRecords("project-1", "missing-context");

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

  const result = await clearProjectContextRecords("project-1", "");

  assert.equal(result, false);
  assert.equal(fetchCalled, false);
});

test("deleteAllProjectContexts deletes all project contexts", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await deleteAllProjectContexts("project-1");

  assert.equal(result, true);
  assert.deepEqual(requests, [
    {
      url: "/api/projects/project-1/contexts",
      init: {
        method: "DELETE",
        headers: {},
        signal: undefined,
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

  const result = await deleteAllProjectContexts("");

  assert.equal(result, false);
  assert.equal(fetchCalled, false);
});

test("getProjectContexts gets context list for a project", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return Response.json({
      data: [
        {
          projectId: "project-1",
          contextId: "ctx-empty",
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
          contextId: "ctx-1",
          jobId: "job-1",
          title: "Tokyo trip",
          latestStatus: 2,
          executionCount: 2,
          messageCount: 4,
          createTime: "2026-01-01T00:00:00Z",
          updateTime: "2026-01-02T00:00:00Z",
          errorMessage: null,
        },
      ],
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await getProjectContexts("project-1");

  assert.equal(result.length, 1);
  assert.equal(result[0].contextId, "ctx-1");
  assert.equal(result[0].jobId, "job-1");
  assert.equal(result[0].executionCount, 2);
  assert.deepEqual(requests, [
    {
      url: "/api/projects/project-1/contexts",
      init: {
        method: "GET",
        headers: {},
        signal: undefined,
      },
    },
  ]);
});

test("getProjectContextDetails encodes context id path parameter", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return Response.json({
      data: {
        projectId: "project-1",
        contextId: "ctx/a b",
        jobId: "job-1",
        title: "Tokyo trip",
        latestStatus: 2,
        executionCount: 2,
        messageCount: 1,
        createTime: "2026-01-01T00:00:00Z",
        updateTime: "2026-01-02T00:00:00Z",
        errorMessage: null,
        messages: [],
      },
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await getProjectContextDetails("project-1", "ctx/a b");

  assert.equal(result.contextId, "ctx/a b");
  assert.equal(result.jobId, "job-1");
  assert.equal(result.executionCount, 2);
  assert.deepEqual(requests, [
    {
      url: "/api/projects/project-1/contexts/ctx%2Fa%20b",
      init: {
        method: "GET",
        headers: {},
        signal: undefined,
      },
    },
  ]);
});

test("updateProjectContextTitle puts the title update for a project context", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await updateProjectContextTitle("project-1", "ctx/a b", "Renamed conversation");

  assert.equal(result, true);
  assert.deepEqual(requests, [
    {
      url: "/api/projects/project-1/contexts/ctx%2Fa%20b/title",
      init: {
        method: "PUT",
        headers: { "content-type": "application/json" },
        signal: undefined,
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

  const result = await updateProjectContextTitle("project-1", "ctx-1", "   ");

  assert.equal(result, false);
  assert.equal(fetchCalled, false);
});

test("deleteProjectContext deletes a project context", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await deleteProjectContext("project-1", "ctx/a b");

  assert.equal(result, true);
  assert.deepEqual(requests, [
    {
      url: "/api/projects/project-1/contexts/ctx%2Fa%20b",
      init: {
        method: "DELETE",
        headers: {},
        signal: undefined,
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

  const result = await deleteProjectContext("project-1", "");

  assert.equal(result, false);
  assert.equal(fetchCalled, false);
});
