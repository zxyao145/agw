import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { after } from "node:test";
import { AddressInfo } from "node:net";

export type ApiRequestRecord = {
  method: string;
  path: string;
  query: URLSearchParams;
  body: unknown;
};

export type ApiRouteHandler = (request: ApiRequestRecord) => unknown;

export type ApiRoutes = Record<string, ApiRouteHandler | unknown>;

export type ApiServer = {
  baseUrl: string;
  requests: ApiRequestRecord[];
  close(): Promise<void>;
};

const ANTIFORGERY_ROUTE = "GET /api/auth/antiforgery";

/**
 * Serves the given routes over real HTTP so components exercise their own fetch path.
 * Route keys are "<METHOD> <path>"; a route value is the payload, or a function returning it.
 * A returned payload is wrapped in the Bens.Results envelope the clients unwrap, and the
 * antiforgery route every write request needs is served unless the caller replaces it.
 * 通过真实 HTTP 提供给定路由，让组件走自身的 fetch 路径。
 * 路由键为 "<方法> <路径>"；路由值是负载，或返回负载的函数。
 * 返回的负载会包进客户端解包所用的 Bens.Results 信封；除非调用方另行提供，
 * 写请求所需的 antiforgery 路由由本服务默认提供。
 */
export async function startApiServer(routes: ApiRoutes): Promise<ApiServer> {
  const requests: ApiRequestRecord[] = [];
  const allRoutes: ApiRoutes = {
    [ANTIFORGERY_ROUTE]: { requestToken: "test-antiforgery-token" },
    ...routes,
  };

  const server = createServer((incoming: IncomingMessage, response: ServerResponse) => {
    const chunks: Buffer[] = [];
    incoming.on("data", (chunk: Buffer) => chunks.push(chunk));
    incoming.on("end", () => {
      const url = new URL(incoming.url ?? "/", "http://localhost");
      const raw = Buffer.concat(chunks).toString("utf8");
      const record: ApiRequestRecord = {
        method: incoming.method ?? "GET",
        path: url.pathname,
        query: url.searchParams,
        body: raw ? parseJson(raw) : undefined,
      };
      requests.push(record);

      const route = allRoutes[`${record.method} ${record.path}`];
      if (route === undefined) {
        response.writeHead(404, { "content-type": "application/json" });
        response.end(JSON.stringify({ code: 4040000, title: "Not Found" }));
        return;
      }

      const data = typeof route === "function" ? (route as ApiRouteHandler)(record) : route;
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ code: 0, title: "OK", data }));
    });
  });

  // A cancelled client request leaves its connection half-open; without short timeouts the
  // process waits out Node's five-minute request timeout before the test file can exit.
  // 被取消的客户端请求会留下半开连接；不缩短超时，进程要等满 Node 的五分钟请求超时才能退出。
  server.keepAliveTimeout = 500;
  server.headersTimeout = 1_000;
  server.requestTimeout = 2_000;

  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  let closed = false;
  const close = async () => {
    if (closed) return;
    closed = true;
    server.closeAllConnections();
    await new Promise<void>((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
  };
  after(close);

  return { baseUrl: `http://127.0.0.1:${port}`, requests, close };
}

function parseJson(raw: string): unknown {
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}
