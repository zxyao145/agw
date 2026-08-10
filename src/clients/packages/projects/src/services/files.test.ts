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

  await listFiles("project-1", "", true, true);

  assert.equal(requests[0].init?.credentials, "same-origin");
  assert.equal(requests[0].url, "/api/files/list?projectId=project-1&diff=true&recursive=true");
});

test("getFileDiff includes the selected git scope", async (t) => {
  const { getFileDiff } = await import("./files" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: string[] = [];

  globalThis.fetch = (async (input: RequestInfo | URL) => {
    requests.push(String(input));
    return new Response(JSON.stringify({ diff: "", unchanged: false }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  await getFileDiff("project-1", "src/file.ts", "staged");

  assert.equal(requests[0], "/api/files/diff?projectId=project-1&path=src%2Ffile.ts&scope=staged");
});

test("setFileStaged calls the stage and unstage endpoints", async (t) => {
  const { clearAntiforgeryToken } = await import("@agw/api");
  const { setFileStaged } = await import("./files" + ".ts");
  const originalFetch = globalThis.fetch;
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  clearAntiforgeryToken();

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    requests.push({ url, init });
    const body =
      url === "/api/auth/antiforgery"
        ? { requestToken: "csrf-token" }
        : { success: true, message: "Updated" };
    return new Response(JSON.stringify(body), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }) as typeof fetch;

  t.after(() => {
    globalThis.fetch = originalFetch;
    clearAntiforgeryToken();
  });

  await setFileStaged("project-1", "src/file.ts", true);
  await setFileStaged("project-1", "src/file.ts", false);

  assert.equal(requests[1].url, "/api/files/stage?projectId=project-1&path=src%2Ffile.ts");
  assert.equal(requests[1].init?.method, "POST");
  assert.equal(requests[2].url, "/api/files/unstage?projectId=project-1&path=src%2Ffile.ts");
  assert.equal(requests[2].init?.method, "POST");
});
