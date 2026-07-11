import assert from "node:assert/strict";
import test from "node:test";

test("listKeysByPair uses same-origin cookie credentials", async (t) => {
  const { listKeysByPair } = await import("./utils" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(JSON.stringify([]), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  await listKeysByPair({ modelProviderId: "mp-1" });

  assert.equal(requests[0].init?.credentials, "same-origin");
  assert.equal(requests[0].url, "/api/model-provider-keys?modelProviderId=mp-1");
});
