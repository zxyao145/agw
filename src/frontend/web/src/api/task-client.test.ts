import assert from "node:assert/strict";
import test from "node:test";

import { clearTaskRecords } from "./task-client";

test("clearTaskRecords deletes records for a project task", async (t) => {
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await clearTaskRecords("task-1", "project-1");

  assert.equal(result, true);
  assert.deepEqual(requests, [
    {
      url: "/api/projects/project-1/tasks/task-1/clear-records",
      init: {
        method: "DELETE",
        headers: {},
        signal: undefined,
      },
    },
  ]);
});

test("clearTaskRecords returns false when the task is not found", async (t) => {
  const originalFetch = globalThis.fetch;

  globalThis.fetch = (async () => new Response(null, { status: 404 })) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await clearTaskRecords("missing-task", "project-1");

  assert.equal(result, false);
});

test("clearTaskRecords returns false when ids are blank", async (t) => {
  const originalFetch = globalThis.fetch;
  let fetchCalled = false;

  globalThis.fetch = (async () => {
    fetchCalled = true;
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await clearTaskRecords("", "project-1");

  assert.equal(result, false);
  assert.equal(fetchCalled, false);
});
