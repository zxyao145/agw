import assert from "node:assert/strict";
import test from "node:test";

test("a delayed antiforgery retry keeps the originating runtime and credentials", async (t) => {
  const { apiPost, configureApiRuntime, resetApiRuntime } = await import("./client.ts");
  const originalFetch = globalThis.fetch;
  const calls: Array<{ url: string; authorization: string | null }> = [];
  let release!: (response: Response) => void;
  let started!: () => void;
  const pendingResponse = new Promise<Response>((resolve) => {
    release = resolve;
  });
  const requestStarted = new Promise<void>((resolve) => {
    started = resolve;
  });
  let writes = 0;
  globalThis.fetch = async (input, init) => {
    const url = String(input);
    calls.push({ url, authorization: new Headers(init?.headers).get("Authorization") });
    if (url.endsWith("/antiforgery")) {
      return Response.json({ code: 0, title: "OK", data: { requestToken: url } });
    }
    if (++writes === 1) {
      started();
      return pendingResponse;
    }
    return Response.json({ code: 0, title: "OK" });
  };
  t.after(() => {
    globalThis.fetch = originalFetch;
    resetApiRuntime();
  });

  configureApiRuntime({ baseUrl: "https://a.example", token: "agw_a" });
  const pending = apiPost("/api/auth/login", { body: { password: "test" } });
  await requestStarted;
  configureApiRuntime({ baseUrl: "https://b.example", token: "agw_b" });
  release(Response.json({ code: 4030003, title: "CSRF" }, { status: 403 }));
  await pending;
  assert.equal(calls.length, 4);
  assert.ok(calls.every((call) => call.url.startsWith("https://a.example/")));
  assert.equal(calls[3]?.authorization, "Bearer agw_a");
  await apiPost("/api/auth/login", { body: { password: "test" } });
  assert.equal(calls[4]?.url, "https://b.example/api/auth/antiforgery");
  assert.equal(calls[5]?.authorization, "Bearer agw_b");
});

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

test("browser API clients wrap fetch transport errors", async (t) => {
  const { ApiTransportError, apiGet, resetApiRuntime } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  resetApiRuntime();
  globalThis.fetch = (async () => {
    throw new Error("network down");
  }) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
    resetApiRuntime();
  });

  await assert.rejects(apiGet("/api/agents"), (caught: unknown) => {
    assert.ok(caught instanceof ApiTransportError);
    assert.equal((caught as { url: string }).url, "/api/agents");
    return true;
  });
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

  await apiDelete("/api/projects/{projectId}/conversations/{conversationId}", {
    params: {
      path: {
        projectId: "project-1",
        conversationId: "11111111-1111-1111-1111-000000000001",
      },
    },
  });

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
    "/api/projects/{projectId}/conversations/{conversationId}/title",
    {
      params: {
        path: {
          projectId: "project-1",
          conversationId: "11111111-1111-1111-1111-000000000002",
        },
      },
      body: { title: "Mobile" },
    },
  );

  assert.deepEqual(result, { saved: true });
  assert.equal(requests.length, 1);
  assert.equal(
    requests[0]?.url,
    "https://mobile.example.com/api/projects/project-1/conversations/11111111-1111-1111-1111-000000000002/title",
  );
  const headers = requests[0]?.init?.headers as Record<string, string> | undefined;
  assert.equal(headers?.Authorization, "Bearer agw_mobile-token");
  assert.equal(headers?.["X-CSRF-TOKEN"], undefined);
  assert.equal(requests[0]?.init?.credentials, "omit");
});

test("explicit OIDC mode never falls back to ambient cookies after Desktop logout", async (t) => {
  const { apiGet, apiPost, configureApiRuntime, resetApiRuntime } = await import("./client.ts");
  const originalFetch = globalThis.fetch;
  const calls: Array<{ url: string; init?: RequestInit }> = [];
  globalThis.fetch = async (input, init) => {
    calls.push({ url: String(input), init });
    return Response.json({
      code: 0,
      title: "ok",
      data: String(input).endsWith("/antiforgery") ? { requestToken: "csrf" } : [],
    });
  };
  t.after(() => {
    globalThis.fetch = originalFetch;
    resetApiRuntime();
  });
  configureApiRuntime({ baseUrl: "http://127.0.0.1:30816", token: null, explicitAuth: true });
  await apiGet("/api/agents");
  await apiPost("/api/auth/logout");
  assert.ok(calls.length >= 3);
  for (const call of calls) {
    assert.equal(call.init?.credentials, "omit");
    assert.equal(new Headers(call.init?.headers).get("X-Agw-Explicit-Auth"), "1");
  }
});

test("apiDownload returns the body bytes with the runtime identity and no antiforgery request", async (t) => {
  const { apiDownload, configureApiRuntime, resetApiRuntime } = await import("./client.ts");
  const originalFetch = globalThis.fetch;
  const calls: Array<{ url: string; init?: RequestInit }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), init });
    return new Response(new Uint8Array([0x00, 0x01, 0xfe, 0xff]), {
      status: 200,
      headers: { "content-type": "application/octet-stream" },
    });
  }) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
    resetApiRuntime();
  });
  configureApiRuntime({
    baseUrl: "http://127.0.0.1:30816",
    token: "agw_desktop",
    explicitAuth: true,
  });

  const bytes = await apiDownload("/api/files/download", {
    params: { query: { projectId: "project-1", path: "assets/logo.png" } },
  });

  assert.ok(bytes instanceof Uint8Array);
  assert.deepEqual([...bytes], [0x00, 0x01, 0xfe, 0xff]);
  assert.equal(calls.length, 1);
  assert.equal(
    calls[0]?.url,
    "http://127.0.0.1:30816/api/files/download?projectId=project-1&path=assets%2Flogo.png",
  );
  const headers = new Headers(calls[0]?.init?.headers);
  assert.equal(calls[0]?.init?.method, "GET");
  assert.equal(headers.get("Authorization"), "Bearer agw_desktop");
  assert.equal(headers.get("X-Agw-Explicit-Auth"), "1");
  assert.equal(headers.get("X-CSRF-TOKEN"), null);
  assert.equal(calls[0]?.init?.credentials, "omit");
});

test("apiDownload throws ApiError whose body is the parsed ApiResult", async (t) => {
  const { ApiError, apiDownload, resetApiRuntime } = await import("./client.ts");
  const originalFetch = globalThis.fetch;
  resetApiRuntime();
  globalThis.fetch = (async () =>
    Response.json(
      { code: 4130001, title: "File exceeds the 100 MiB transfer limit." },
      {
        status: 413,
        statusText: "Payload Too Large",
      },
    )) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
    resetApiRuntime();
  });

  await assert.rejects(
    apiDownload("/api/files/download", {
      params: { query: { projectId: "project-1", path: "a" } },
    }),
    (caught: unknown) => {
      assert.ok(caught instanceof ApiError);
      assert.equal(caught.status, 413);
      assert.equal(caught.url, "/api/files/download?projectId=project-1&path=a");
      assert.deepEqual(caught.body, {
        code: 4130001,
        title: "File exceeds the 100 MiB transfer limit.",
      });
      return true;
    },
  );
});

test("apiDownload wraps fetch failures in ApiTransportError", async (t) => {
  const { ApiTransportError, apiDownload, resetApiRuntime } = await import("./client.ts");
  const originalFetch = globalThis.fetch;
  resetApiRuntime();
  globalThis.fetch = (async () => {
    throw new Error("network down");
  }) as typeof fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
    resetApiRuntime();
  });

  await assert.rejects(
    apiDownload("/api/files/download", { params: { query: { projectId: "project-1" } } }),
    (caught: unknown) => caught instanceof ApiTransportError,
  );
});

test("Bearer API clients download bytes and report ApiResult failures", async (t) => {
  const { ApiError, createBearerApiClient } = await import("./client.ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return String(input).includes("path=missing")
      ? Response.json({ code: 4040007, title: "File not found" }, { status: 404 })
      : new Response(new Uint8Array([0x42, 0x00]), {
          headers: { "content-type": "application/octet-stream" },
        });
  }) as typeof fetch;
  t.after(() => (globalThis.fetch = originalFetch));
  let unauthorized = 0;
  const client = createBearerApiClient({
    baseUrl: "https://mobile.example.com/",
    token: "agw_mobile",
    onUnauthorized: () => (unauthorized += 1),
  });

  const bytes = await client.apiDownload("/api/files/download", {
    params: { query: { projectId: "project-1", path: "b.bin" } },
  });
  await assert.rejects(
    client.apiDownload("/api/files/download", {
      params: { query: { projectId: "project-1", path: "missing" } },
    }),
    (caught: unknown) => {
      assert.ok(caught instanceof ApiError);
      assert.equal(caught.status, 404);
      assert.deepEqual(caught.body, { code: 4040007, title: "File not found" });
      return true;
    },
  );

  assert.deepEqual([...bytes], [0x42, 0x00]);
  assert.equal(
    requests[0]?.url,
    "https://mobile.example.com/api/files/download?projectId=project-1&path=b.bin",
  );
  assert.equal(
    (requests[0]?.init?.headers as Record<string, string> | undefined)?.Authorization,
    "Bearer agw_mobile",
  );
  assert.equal(requests[0]?.init?.credentials, "omit");
  assert.equal(unauthorized, 0);
});
