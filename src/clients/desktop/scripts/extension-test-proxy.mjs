import { once } from "node:events";
import { createServer, request as httpRequest } from "node:http";
import { connect } from "node:net";

async function listen(server) {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return server.address().port;
}

function requestOptions(request, hostPort) {
  return {
    hostname: "127.0.0.1",
    port: hostPort,
    path: request.url ?? "/",
    method: request.method,
    headers: request.headers,
  };
}

/** 用 HTTP request 事件逐条检查 keep-alive 请求，并通过 upgrade 事件转发 SignalR WebSocket。
 * Gates each keep-alive request through HTTP events and forwards SignalR WebSockets through upgrade events. */
export async function startHubProxy(hostPort) {
  let delayHub = false;
  let delayCatalog = false;
  let held = 0;
  let heldClosed = 0;
  let catalogHeld = 0;
  let pendingCatalog = 0;
  let hubOpen = 0;
  const catalogRequests = [];
  const pendingCatalogRequests = new Set();

  const forwardRequest = (request, response) => {
    if (request.aborted || response.destroyed) return;
    const upstream = httpRequest(requestOptions(request, hostPort), (upstreamResponse) => {
      if (response.destroyed) return;
      response.writeHead(
        upstreamResponse.statusCode ?? 502,
        upstreamResponse.statusMessage,
        upstreamResponse.headers,
      );
      upstreamResponse.pipe(response);
    });
    upstream.on("error", () => response.destroy());
    request.once("aborted", () => upstream.destroy());
    response.once("close", () => {
      if (!response.writableFinished) upstream.destroy();
    });
    request.pipe(upstream);
  };

  const server = createServer((request, response) => {
    const isCatalog =
      request.method === "GET" && /^\/api\/extensions\/catalog(?:\?|$)/u.test(request.url ?? "");
    if (isCatalog) catalogRequests.push(`${request.method} ${request.url}`);
    if (!isCatalog || !delayCatalog) {
      forwardRequest(request, response);
      return;
    }

    catalogHeld += 1;
    pendingCatalog += 1;
    const pending = { request, response };
    const removePending = () => {
      if (pendingCatalogRequests.delete(pending)) pendingCatalog -= 1;
    };
    pending.removePending = removePending;
    pendingCatalogRequests.add(pending);
    request.once("aborted", removePending);
    response.once("close", removePending);
  });

  server.on("upgrade", (request, clientSocket, head) => {
    const isHub = (request.url ?? "").startsWith("/api/hubs/exec");
    if (isHub && delayHub) {
      held += 1;
      clientSocket.on("error", () => undefined);
      clientSocket.once("close", () => (heldClosed += 1));
      clientSocket.once("end", () => clientSocket.destroy());
      clientSocket.resume();
      return;
    }

    const upstream = connect(hostPort, "127.0.0.1", () => {
      if (isHub) hubOpen += 1;
      upstream.write(`${request.method} ${request.url ?? "/"} HTTP/${request.httpVersion}\r\n`);
      for (let index = 0; index < request.rawHeaders.length; index += 2) {
        upstream.write(`${request.rawHeaders[index]}: ${request.rawHeaders[index + 1]}\r\n`);
      }
      upstream.write("\r\n");
      if (head.length > 0) upstream.write(head);
      clientSocket.pipe(upstream);
      upstream.pipe(clientSocket);
    });
    upstream.once("close", () => {
      if (isHub) hubOpen -= 1;
      clientSocket.destroy();
    });
    upstream.on("error", () => clientSocket.destroy());
    clientSocket.on("error", () => upstream.destroy());
    clientSocket.once("close", () => upstream.destroy());
    clientSocket.pause();
  });

  return {
    server,
    port: await listen(server),
    setDelay: (enabled) => (delayHub = enabled),
    setCatalogDelay: (enabled) => {
      delayCatalog = enabled;
      if (enabled) return;
      for (const pending of pendingCatalogRequests) {
        pending.request.off("aborted", pending.removePending);
        pending.response.off("close", pending.removePending);
        pending.removePending();
        forwardRequest(pending.request, pending.response);
      }
    },
    state: () => ({
      delayHub,
      held,
      heldClosed,
      hubOpen,
      delayCatalog,
      catalogHeld,
      pendingCatalog,
      catalogRequests,
    }),
  };
}
