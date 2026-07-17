import assert from "node:assert/strict";
import test from "node:test";

import { createLocalDesktopToken } from "./local-token";

test("createLocalDesktopToken obtains antiforgery state before creating a token", async () => {
  const requests: Array<{ url: string; init?: RequestInit }> = [];
  const fetcher = async (input: string | Request, init?: RequestInit): Promise<Response> => {
    const url = String(input);
    requests.push({ url, init });
    if (url.endsWith("/api/auth/antiforgery")) {
      return Response.json({ code: 0, title: "OK", data: { requestToken: "csrf-desktop" } });
    }
    return Response.json({
      code: 0,
      title: "OK",
      data: { token: "agw_created-token" },
    });
  };

  const token = await createLocalDesktopToken(
    fetcher,
    "http://127.0.0.1:30815/",
    "Agw Desktop test",
  );

  assert.equal(token, "agw_created-token");
  assert.equal(requests[0]?.url, "http://127.0.0.1:30815/api/auth/antiforgery");
  assert.equal(requests[0]?.init?.credentials, "include");
  assert.equal(requests[1]?.url, "http://127.0.0.1:30815/api/auth/tokens");
  assert.equal(requests[1]?.init?.method, "POST");
  const createHeaders = requests[1]?.init?.headers as Record<string, string> | undefined;
  assert.equal(createHeaders?.["X-CSRF-TOKEN"], "csrf-desktop");
  assert.deepEqual(JSON.parse(String(requests[1]?.init?.body)), { name: "Agw Desktop test" });
});

test("createLocalDesktopToken rejects malformed API responses", async () => {
  await assert.rejects(
    () =>
      createLocalDesktopToken(
        async () => Response.json({ code: 0, title: "OK", data: {} }),
        "http://127.0.0.1:30815",
        "Agw Desktop test",
      ),
    /antiforgery/u,
  );
});
