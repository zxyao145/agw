import assert from "node:assert/strict";
import test from "node:test";

test("listFiles uses same-origin cookie credentials", async (t) => {
  const { listFiles } = await import("./files" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ url: String(input), init });
    return new Response(JSON.stringify({ items: [] }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  await listFiles("/workspace", true, true);

  assert.equal(requests[0].init?.credentials, "same-origin");
  assert.equal(requests[0].url, "/api/files/list?path=%2Fworkspace&diff=true&recursive=true");
});
