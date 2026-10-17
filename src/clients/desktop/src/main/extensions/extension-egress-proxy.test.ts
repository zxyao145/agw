import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer as createHttpServer, request, type Server } from "node:http";
import { createServer as createTcpServer, connect, type Socket } from "node:net";
import test from "node:test";

import { parseHostPermissions } from "../../shared/extension-host-permissions";
import { type EgressBlockedEvent, ExtensionEgressProxy } from "./extension-egress-proxy";

async function listen(server: Server | ReturnType<typeof createTcpServer>): Promise<number> {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("No address.");
  return address.port;
}

/** 通过代理发出 CONNECT，返回状态码与隧道。Sends CONNECT through the proxy and returns the status and tunnel. */
async function connectThrough(
  proxyPort: number,
  target: string,
): Promise<{ status: number; socket: Socket }> {
  const connectRequest = request({
    host: "127.0.0.1",
    port: proxyPort,
    method: "CONNECT",
    path: target,
  });
  connectRequest.end();
  const [response, socket] = (await once(connectRequest, "connect")) as [
    { statusCode: number },
    Socket,
  ];
  return { status: response.statusCode, socket };
}

async function echoServer(): Promise<{
  port: number;
  connections: Socket[];
  close(): Promise<void>;
}> {
  const connections: Socket[] = [];
  const server = createTcpServer((socket) => {
    connections.push(socket);
    socket.on("error", () => undefined);
    socket.pipe(socket);
  });
  const port = await listen(server);
  return {
    port,
    connections,
    close: async () => {
      for (const socket of connections) socket.destroy();
      server.close();
      await once(server, "close");
    },
  };
}

test("CONNECT is tunneled only to approved host and port pairs", async () => {
  const allowed = await echoServer();
  const denied = await echoServer();
  const blocked: EgressBlockedEvent[] = [];
  const proxy = new ExtensionEgressProxy({
    getPermissions: () => parseHostPermissions([`https://127.0.0.1:${allowed.port}`]),
    resolveUpstream: async () => "DIRECT",
    onBlocked: (event) => blocked.push(event),
  });
  const proxyPort = await proxy.start();
  try {
    const tunnel = await connectThrough(proxyPort, `127.0.0.1:${allowed.port}`);
    assert.equal(tunnel.status, 200);
    tunnel.socket.write("ping");
    const [data] = (await once(tunnel.socket, "data")) as [Buffer];
    assert.equal(data.toString(), "ping");
    tunnel.socket.destroy();

    const refused = await connectThrough(proxyPort, `127.0.0.1:${denied.port}`);
    assert.equal(refused.status, 403);
    refused.socket.destroy();
    assert.equal(denied.connections.length, 0);
    assert.deepEqual(blocked, [
      { url: `127.0.0.1:${denied.port}`, resourceType: "connect", reason: "host-not-permitted" },
    ]);
  } finally {
    await proxy.close();
    await allowed.close();
    await denied.close();
  }
});

test("absolute-URL HTTP requests are forwarded for approved http origins only", async () => {
  const requests: string[] = [];
  const target = createHttpServer((incoming, response) => {
    requests.push(`${incoming.method} ${incoming.url}`);
    response.end("hello");
  });
  const targetPort = await listen(target);
  const blocked: EgressBlockedEvent[] = [];
  const proxy = new ExtensionEgressProxy({
    getPermissions: () => parseHostPermissions([`http://127.0.0.1:${targetPort}`]),
    resolveUpstream: async () => "DIRECT",
    onBlocked: (event) => blocked.push(event),
  });
  const proxyPort = await proxy.start();
  try {
    const send = (url: string) =>
      new Promise<{ status: number; body: string }>((resolve, reject) => {
        const outgoing = request(
          { host: "127.0.0.1", port: proxyPort, path: url, method: "GET" },
          (response) => {
            let body = "";
            response.on("data", (chunk) => (body += chunk));
            response.on("end", () => resolve({ status: response.statusCode ?? 0, body }));
          },
        );
        outgoing.on("error", reject);
        outgoing.end();
      });
    assert.deepEqual(await send(`http://127.0.0.1:${targetPort}/a?b=1`), {
      status: 200,
      body: "hello",
    });
    assert.deepEqual(requests, ["GET /a?b=1"]);
    assert.equal((await send("http://localhost:1/other")).status, 403);
    assert.equal((await send("/relative")).status, 400);
    assert.deepEqual(blocked, [
      { url: "http://localhost:1/other", resourceType: "http", reason: "host-not-permitted" },
    ]);
  } finally {
    await proxy.close();
    target.close();
  }
});

test("upstream HTTP proxies are chained with CONNECT and unsupported ones are refused", async () => {
  const destination = await echoServer();
  const upstreamTunnels: string[] = [];
  const upstream = createHttpServer();
  upstream.on("connect", (incoming, socket: Socket, head: Buffer) => {
    upstreamTunnels.push(incoming.url ?? "");
    const [host, port] = (incoming.url ?? "").split(":");
    const target = connect(Number(port), host, () => {
      socket.write("HTTP/1.1 200 Connection Established\r\n\r\n");
      if (head.length > 0) target.write(head);
      target.pipe(socket);
      socket.pipe(target);
    });
    target.on("error", () => socket.destroy());
    socket.on("error", () => target.destroy());
  });
  const upstreamPort = await listen(upstream);
  let resolved = `PROXY 127.0.0.1:${upstreamPort}`;
  const blocked: EgressBlockedEvent[] = [];
  const proxy = new ExtensionEgressProxy({
    getPermissions: () => parseHostPermissions([`https://127.0.0.1:${destination.port}`]),
    resolveUpstream: async () => resolved,
    onBlocked: (event) => blocked.push(event),
  });
  const proxyPort = await proxy.start();
  try {
    const tunnel = await connectThrough(proxyPort, `127.0.0.1:${destination.port}`);
    assert.equal(tunnel.status, 200);
    tunnel.socket.write("via-upstream");
    const [data] = (await once(tunnel.socket, "data")) as [Buffer];
    assert.equal(data.toString(), "via-upstream");
    assert.deepEqual(upstreamTunnels, [`127.0.0.1:${destination.port}`]);
    tunnel.socket.destroy();

    resolved = "SOCKS5 127.0.0.1:1080";
    const refused = await connectThrough(proxyPort, `127.0.0.1:${destination.port}`);
    assert.equal(refused.status, 403);
    refused.socket.destroy();
    assert.deepEqual(blocked, [
      {
        url: `127.0.0.1:${destination.port}`,
        resourceType: "connect",
        reason: "upstream-proxy-unsupported",
      },
    ]);
  } finally {
    await proxy.close();
    upstream.close();
    await destination.close();
  }
});

test("closing the proxy stops listening and closes open tunnels", async () => {
  const destination = await echoServer();
  const proxy = new ExtensionEgressProxy({
    getPermissions: () => parseHostPermissions([`https://127.0.0.1:${destination.port}`]),
    resolveUpstream: async () => "DIRECT",
    onBlocked: () => undefined,
  });
  const proxyPort = await proxy.start();
  const tunnel = await connectThrough(proxyPort, `127.0.0.1:${destination.port}`);
  assert.equal(tunnel.status, 200);
  tunnel.socket.write("ready");
  await once(tunnel.socket, "data");
  const tunnelClosed = once(tunnel.socket, "close");
  const destinationClosed = once(destination.connections[0], "close");
  await proxy.close();
  await tunnelClosed;
  await destinationClosed;
  await assert.rejects(
    new Promise((resolve, reject) => {
      const socket = connect(proxyPort, "127.0.0.1", () => resolve(socket));
      socket.on("error", reject);
    }),
  );
  await destination.close();
});
