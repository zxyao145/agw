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
  const { apiPost, clearAntiforgeryToken } = await import("./client" + ".ts");
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
