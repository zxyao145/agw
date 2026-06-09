import assert from "node:assert/strict";
import test from "node:test";

test("listFiles attaches X-API-Key header from unified API client", async (t) => {
  const { setApiKey } = await import("./client" + ".ts");
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

  setApiKey("file-key-123");

  t.after(() => {
    globalThis.fetch = originalFetch;
    setApiKey(null);
  });

  await listFiles("/workspace", true, true);

  const headers = requests[0].init?.headers as Record<string, string> | undefined;
  assert.equal(headers?.["X-API-Key"], "file-key-123");
  assert.equal(requests[0].url, "/api/files/list?path=%2Fworkspace&diff=true&recursive=true");
});
