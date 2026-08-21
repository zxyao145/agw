import assert from "node:assert/strict";
import test from "node:test";

test("apiGet unwraps Bens.Results data envelopes", async (t) => {
  const { apiGet } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  globalThis.fetch = (async () =>
    new Response(JSON.stringify({ code: 0, title: "OK", data: [{ id: "agent-1" }] }), {
      status: 200,
      headers: { "content-type": "application/json" },
    })) as typeof fetch;
  t.after(() => (globalThis.fetch = originalFetch));

  assert.deepEqual(await apiGet("/api/agents"), [{ id: "agent-1" }]);
});

test("apiPost obtains and attaches an antiforgery token", async (t) => {
  const { apiPost, clearAntiforgeryToken, resetApiRuntime } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  clearAntiforgeryToken();
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    if (String(input) === "/api/auth/antiforgery") {
      return new Response(
        JSON.stringify({ code: 0, title: "OK", data: { requestToken: "csrf-123" } }),
        {
          status: 200,
          headers: { "content-type": "application/json" },
        },
      );
    }
    return new Response(JSON.stringify({ code: 0, title: "OK" }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
    clearAntiforgeryToken();
    resetApiRuntime();
  });

  await apiPost("/api/agents" as never, { body: {} } as never);

  assert.equal(requests[0]?.url, "/api/auth/antiforgery");
  assert.ok(requests[1]);
  const headers = requests[1].init?.headers;
  assert.ok(headers);
  assert.equal((headers as Record<string, string>)["X-CSRF-TOKEN"], "csrf-123");
  assert.equal(requests[1]?.init?.credentials, "same-origin");

  clearAntiforgeryToken();
  await apiPost("/api/agents" as never, { body: {} } as never);
  assert.equal(requests[2]?.url, "/api/auth/antiforgery");
});

test("desktop API runtime obtains antiforgery before writes and keeps Bearer authentication", async (t) => {
  const { apiPost, configureApiRuntime, resetApiRuntime } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  configureApiRuntime({ baseUrl: "http://127.0.0.1:30815", token: "agw_desktop-token" });
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    if (String(input).endsWith("/api/auth/antiforgery")) {
      return new Response(
        JSON.stringify({ code: 0, title: "OK", data: { requestToken: "csrf-desktop" } }),
        {
          status: 200,
          headers: { "content-type": "application/json" },
        },
      );
    }
    return new Response(JSON.stringify({ code: 0, title: "OK" }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
    resetApiRuntime();
  });

  await apiPost("/api/agents" as never, { body: {} } as never);

  assert.equal(requests.length, 2);
  assert.equal(requests[0]?.url, "http://127.0.0.1:30815/api/auth/antiforgery");
  assert.equal(requests[0]?.init?.credentials, "omit");
  assert.equal(requests[1]?.url, "http://127.0.0.1:30815/api/agents");
  const headers = requests[1]?.init?.headers as Record<string, string> | undefined;
  assert.equal(headers?.Authorization, "Bearer agw_desktop-token");
  assert.equal(headers?.["X-CSRF-TOKEN"], "csrf-desktop");
  assert.equal(requests[1]?.init?.credentials, "omit");
});

test("desktop API runtime includes antiforgery cookies when no Bearer token is available", async (t) => {
  const { apiPost, configureApiRuntime, resetApiRuntime } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  configureApiRuntime({ baseUrl: "http://127.0.0.1:30815", token: null });
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(
      String(input).endsWith("/api/auth/antiforgery")
        ? JSON.stringify({ code: 0, title: "OK", data: { requestToken: "csrf-local" } })
        : JSON.stringify({ code: 0, title: "OK" }),
      { status: 200, headers: { "content-type": "application/json" } },
    );
  }) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
    resetApiRuntime();
  });

  await apiPost("/api/agents" as never, { body: {} } as never);

  assert.equal(requests[0]?.init?.credentials, "include");
  assert.equal(requests[1]?.init?.credentials, "include");
});

test("desktop DELETE refreshes stale antiforgery state and retries once", async (t) => {
  const { apiDelete, configureApiRuntime, resetApiRuntime } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  let antiforgeryRequestCount = 0;
  let deleteRequestCount = 0;
  configureApiRuntime({ baseUrl: "http://127.0.0.1:30815", token: null });
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    requests.push({ url, init });
    if (url.endsWith("/api/auth/antiforgery")) {
      antiforgeryRequestCount += 1;
      return Response.json({
        code: 0,
        title: "OK",
        data: { requestToken: `csrf-${antiforgeryRequestCount}` },
      });
    }

    deleteRequestCount += 1;
    return deleteRequestCount === 1
      ? Response.json(
          { code: 4030003, title: "Antiforgery validation failed.", statusCode: 403 },
          { status: 403 },
        )
      : Response.json({ code: 0, title: "OK" });
  }) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
    resetApiRuntime();
  });

  await apiDelete(
    "/api/projects/{projectId}/contexts/{contextId}" as never,
    {
      params: { path: { projectId: "project-1", contextId: "context-1" } },
    } as never,
  );

  assert.equal(antiforgeryRequestCount, 2);
  assert.equal(deleteRequestCount, 2);
  assert.equal(requests.length, 4);
  assert.equal(
    (requests[1]?.init?.headers as Record<string, string> | undefined)?.["X-CSRF-TOKEN"],
    "csrf-1",
  );
  assert.equal(
    (requests[3]?.init?.headers as Record<string, string> | undefined)?.["X-CSRF-TOKEN"],
    "csrf-2",
  );
});

test("Bearer API clients are isolated and never request antiforgery tokens", async (t) => {
  const { createBearerApiClient } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return Response.json({ code: 0, title: "OK", data: { saved: true } });
  }) as typeof fetch;
  t.after(() => (globalThis.fetch = originalFetch));

  const client = createBearerApiClient({
    baseUrl: "https://mobile.example.com/",
    token: " agw_mobile-token ",
  });
  const result = await client.apiPut(
    "/api/projects/{projectId}/contexts/{contextId}/title" as never,
    {
      params: { path: { projectId: "project-1", contextId: "context/1" } },
      body: { title: "Mobile" },
    } as never,
  );

  assert.deepEqual(result, { saved: true });
  assert.equal(requests.length, 1);
  assert.equal(
    requests[0]?.url,
    "https://mobile.example.com/api/projects/project-1/contexts/context%2F1/title",
  );
  const headers = requests[0]?.init?.headers as Record<string, string> | undefined;
  assert.equal(headers?.Authorization, "Bearer agw_mobile-token");
  assert.equal(headers?.["X-CSRF-TOKEN"], undefined);
  assert.equal(requests[0]?.init?.credentials, "omit");
});
