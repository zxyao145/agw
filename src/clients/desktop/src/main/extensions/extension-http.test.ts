import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import test from "node:test";

import { parseHostPermissions } from "../../shared/extension-host-permissions";
import { ExtensionHttpClient, fetchHttpTransport } from "./extension-http";

type Handler = (request: IncomingMessage, response: ServerResponse, body: Buffer) => void;

async function startServer(handler: Handler) {
  const closedRequests: string[] = [];
  const server = createServer(async (request, response) => {
    request.socket.once("close", () => closedRequests.push(request.url ?? ""));
    const chunks: Buffer[] = [];
    for await (const chunk of request) chunks.push(chunk as Buffer);
    handler(request, response, Buffer.concat(chunks));
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("No address.");
  return {
    origin: `http://127.0.0.1:${address.port}`,
    closedRequests,
    close: async () => {
      server.closeAllConnections();
      server.close();
      await once(server, "close");
    },
  };
}

const owner = (extensionId: string, requestNumber = 1) => ({
  extensionId,
  frameId: `${extensionId}-frame`,
  pageId: `${extensionId}-page-${requestNumber}`,
});

test("requests carry the method, headers and body and return binary bodies and non-2xx statuses", async () => {
  const seen: { method?: string; header?: string; body?: string }[] = [];
  const server = await startServer((request, response, body) => {
    seen.push({
      method: request.method,
      header: request.headers["x-test"] as string,
      body: body.toString(),
    });
    if (request.url === "/missing") {
      response.writeHead(404, { "content-type": "text/plain" }).end("nope");
      return;
    }
    response
      .writeHead(200, { "content-type": "application/octet-stream" })
      .end(Buffer.from([0, 1, 2, 255]));
  });
  const client = new ExtensionHttpClient(() => fetchHttpTransport(fetch));
  const permissions = parseHostPermissions([server.origin]);
  try {
    const result = await client.request(owner("sample"), permissions, {
      requestId: "r1",
      url: `${server.origin}/upload`,
      method: "POST",
      headers: { "x-test": "yes", "content-type": "application/json" },
      body: '{"ok":true}',
    });
    assert.equal(result.status, 200);
    assert.equal(result.headers["content-type"], "application/octet-stream");
    assert.deepEqual(result.body, new Uint8Array([0, 1, 2, 255]));
    assert.deepEqual(seen[0], { method: "POST", header: "yes", body: '{"ok":true}' });

    const binaryBody = await client.request(owner("sample"), permissions, {
      requestId: "r2",
      url: `${server.origin}/binary`,
      method: "PUT",
      body: new Uint8Array([9, 8, 7]),
    });
    assert.equal(binaryBody.status, 200);
    assert.equal(seen[1].body, Buffer.from([9, 8, 7]).toString());

    const missing = await client.request(owner("sample"), permissions, {
      requestId: "r3",
      url: `${server.origin}/missing`,
    });
    assert.equal(missing.status, 404);
    assert.equal(new TextDecoder().decode(missing.body), "nope");
    assert.equal(
      client.count(() => true),
      0,
    );
  } finally {
    await server.close();
  }
});

test("origins that are not approved are rejected before sending", async () => {
  let hits = 0;
  const server = await startServer((_request, response) => {
    hits += 1;
    response.end();
  });
  const client = new ExtensionHttpClient(() => fetchHttpTransport(fetch));
  try {
    await assert.rejects(
      client.request(owner("sample"), parseHostPermissions(["https://api.example.com"]), {
        requestId: "r1",
        url: `${server.origin}/`,
      }),
      { code: "ERR_HOST_NOT_PERMITTED" },
    );
    await assert.rejects(
      client.request(owner("sample"), parseHostPermissions([server.origin]), {
        requestId: "r2",
        url: "ftp://example.com/",
      }),
      { code: "ERR_HOST_NOT_PERMITTED" },
    );
    assert.equal(hits, 0);
  } finally {
    await server.close();
  }
});

test("redirects are checked against the approved hosts, 303 becomes GET, and more than five fail", async () => {
  const methods: string[] = [];
  const server = await startServer((request, response) => {
    methods.push(`${request.method} ${request.url}`);
    const url = new URL(request.url ?? "/", "http://local");
    if (url.pathname === "/outside") {
      response.writeHead(302, { location: "https://evil.example/steal" }).end();
    } else if (url.pathname === "/see-other") {
      response.writeHead(303, { location: "/done" }).end();
    } else if (url.pathname === "/loop") {
      const count = Number(url.searchParams.get("n") ?? "0");
      response.writeHead(307, { location: `/loop?n=${count + 1}` }).end();
    } else {
      response.end("done");
    }
  });
  const client = new ExtensionHttpClient(() => fetchHttpTransport(fetch));
  const permissions = parseHostPermissions([server.origin]);
  try {
    await assert.rejects(
      client.request(owner("sample"), permissions, {
        requestId: "r1",
        url: `${server.origin}/outside`,
      }),
      { code: "ERR_HOST_NOT_PERMITTED" },
    );
    const seeOther = await client.request(owner("sample"), permissions, {
      requestId: "r2",
      url: `${server.origin}/see-other`,
      method: "POST",
      body: "x",
    });
    assert.equal(new TextDecoder().decode(seeOther.body), "done");
    assert.deepEqual(methods.slice(1, 3), ["POST /see-other", "GET /done"]);
    await assert.rejects(
      client.request(owner("sample"), permissions, {
        requestId: "r3",
        url: `${server.origin}/loop`,
      }),
      { code: "ERR_TOO_MANY_REDIRECTS" },
    );
    assert.equal(methods.filter((line) => line.includes("/loop")).length, 6);
  } finally {
    await server.close();
  }
});

test("canceling by request id aborts a hanging request and closes its connection", async () => {
  const server = await startServer(() => undefined);
  const client = new ExtensionHttpClient(() => fetchHttpTransport(fetch));
  try {
    const pending = client.request(owner("sample"), parseHostPermissions([server.origin]), {
      requestId: "hang",
      url: `${server.origin}/hang`,
    });
    while (client.count(() => true) === 0) await new Promise((resolve) => setTimeout(resolve, 5));
    await new Promise((resolve) => setTimeout(resolve, 50));
    client.cancel("hang");
    await assert.rejects(pending, { code: "ABORT_ERR" });
    for (let attempt = 0; attempt < 100 && !server.closedRequests.includes("/hang"); attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.deepEqual(server.closedRequests, ["/hang"]);
    assert.equal(
      client.count(() => true),
      0,
    );
    client.cancel("hang");
  } finally {
    await server.close();
  }
});

test("releasing an extension aborts only that extension's requests", async () => {
  const server = await startServer((request, response) => {
    if (request.url === "/fast") response.end("ok");
  });
  const client = new ExtensionHttpClient(() => fetchHttpTransport(fetch));
  const permissions = parseHostPermissions([server.origin]);
  try {
    const first = client.request(owner("first"), permissions, {
      requestId: "a",
      url: `${server.origin}/hang`,
    });
    const second = client.request(owner("first", 2), permissions, {
      requestId: "b",
      url: `${server.origin}/hang`,
    });
    const other = client.request(owner("other"), permissions, {
      requestId: "c",
      url: `${server.origin}/hang`,
    });
    while (client.count(() => true) < 3) await new Promise((resolve) => setTimeout(resolve, 5));
    client.releaseExtension("first");
    await assert.rejects(first, { code: "ABORT_ERR" });
    await assert.rejects(second, { code: "ABORT_ERR" });
    assert.equal(
      client.count((item) => item.extensionId === "other"),
      1,
    );
    client.releaseFrame("other", "other-frame");
    await assert.rejects(other, { code: "ABORT_ERR" });
    assert.equal(
      client.count(() => true),
      0,
    );
  } finally {
    await server.close();
  }
});

test("responses over the size limit fail with ERR_RESPONSE_TOO_LARGE", async () => {
  const server = await startServer((_request, response) => response.end(Buffer.alloc(2048)));
  const client = new ExtensionHttpClient(() => fetchHttpTransport(fetch), 1024);
  try {
    await assert.rejects(
      client.request(owner("sample"), parseHostPermissions([server.origin]), {
        requestId: "big",
        url: `${server.origin}/`,
      }),
      { code: "ERR_RESPONSE_TOO_LARGE" },
    );
  } finally {
    await server.close();
  }
});
