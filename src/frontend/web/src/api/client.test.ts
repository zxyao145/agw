import assert from "node:assert/strict";
import test from "node:test";

test("apiGet unwraps Bens.Results data envelopes", async (t) => {
  const { apiGet } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;

  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({
        code: 0,
        title: "OK",
        data: [{ id: "agent-1", name: "agent" }],
      }),
      {
        status: 200,
        headers: { "content-type": "application/json" },
      },
    )) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const result = await apiGet("/api/agents");

  assert.deepEqual(result, [{ id: "agent-1", name: "agent" }]);
});

test("apiRequest attaches X-API-Key header when apiKey is set", async (t) => {
  const { apiGet, setApiKey } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  setApiKey("test-key-123");

  t.after(() => {
    globalThis.fetch = originalFetch;
    setApiKey(null);
  });

  await apiGet("/api/agents");

  const headers = requests[0].init?.headers as Record<string, string>;
  assert.equal(headers["X-API-Key"], "test-key-123");
});

test("apiRequest omits X-API-Key header when apiKey is null", async (t) => {
  const { apiGet, setApiKey } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  setApiKey(null);

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  await apiGet("/api/agents");

  const headers = requests[0].init?.headers as Record<string, string>;
  assert.equal(headers["X-API-Key"], undefined);
});

test("apiRequest lets caller-supplied X-API-Key header override the cached value", async (t) => {
  const { apiGet, setApiKey } = await import("./client" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  setApiKey("cached-key");

  t.after(() => {
    globalThis.fetch = originalFetch;
    setApiKey(null);
  });

  await apiGet("/api/agents", { headers: { "X-API-Key": "override-key" } });

  const headers = requests[0].init?.headers as Record<string, string>;
  assert.equal(headers["X-API-Key"], "override-key");
});
