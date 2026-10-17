import {
  createServer,
  request as httpRequest,
  type IncomingMessage,
  type Server,
  type ServerResponse,
} from "node:http";
import { request as httpsRequest } from "node:https";
import { connect, type Socket } from "node:net";
import type { Duplex } from "node:stream";

import type { ExtensionRequestBlockedReason } from "../../shared/contracts";
import type { HostPermission } from "../../shared/extension-host-permissions";
import {
  isConnectAllowed,
  isProxyHttpRequestAllowed,
  parseResolveProxyResult,
  type UpstreamProxy,
} from "./extension-network-rules";

export type EgressBlockedEvent = {
  url: string;
  resourceType: "connect" | "http";
  reason: Extract<
    ExtensionRequestBlockedReason,
    "host-not-permitted" | "upstream-proxy-unsupported"
  >;
};

export type ExtensionEgressProxyOptions = {
  getPermissions(): readonly HostPermission[];
  /** 生产环境为 `session.defaultSession.resolveProxy`。`session.defaultSession.resolveProxy` in production. */
  resolveUpstream(url: string): Promise<string>;
  onBlocked(event: EgressBlockedEvent): void;
};

const HOP_BY_HOP_HEADERS = ["proxy-connection", "proxy-authorization", "connection", "keep-alive"];

function splitHostPort(value: string): { host: string; port: number } | null {
  const match = /^(\[[^\]]+\]|[^:]+):(\d{1,5})$/u.exec(value);
  if (!match) return null;
  const port = Number(match[2]);
  if (port < 1 || port > 65_535) return null;
  return { host: match[1].replace(/^\[|\]$/gu, "").toLowerCase(), port };
}

/** 直连或经上游 HTTP 代理 `CONNECT` 建立到目标的 TCP 连接。Opens a TCP connection directly or through an upstream HTTP proxy `CONNECT`. */
function openTunnel(upstream: UpstreamProxy, host: string, port: number): Promise<Duplex> {
  if (upstream.type === "unsupported")
    throw new Error(`Unsupported upstream proxy: ${upstream.value}`);
  if (upstream.type === "direct") {
    return new Promise((resolve, reject) => {
      const socket: Socket = connect({ host, port });
      socket.once("connect", () => {
        socket.off("error", reject);
        resolve(socket);
      });
      socket.once("error", reject);
    });
  }
  return new Promise((resolve, reject) => {
    const request = (upstream.secure ? httpsRequest : httpRequest)({
      host: upstream.host,
      port: upstream.port,
      method: "CONNECT",
      path: `${host.includes(":") ? `[${host}]` : host}:${port}`,
      headers: { Host: `${host}:${port}` },
      agent: false,
    });
    request.once("connect", (response: IncomingMessage, socket: Socket, head: Buffer) => {
      if (response.statusCode !== 200) {
        socket.destroy();
        reject(new Error(`Upstream proxy refused CONNECT with ${response.statusCode}.`));
        return;
      }
      if (head.length > 0) socket.unshift(head);
      resolve(socket);
    });
    request.once("error", reject);
    request.end();
  });
}

/** 一个 extension 的出站代理，只放行已批准的域名。One extension's egress proxy that only admits approved hosts. */
export class ExtensionEgressProxy {
  private readonly server: Server;
  private readonly sockets = new Set<Duplex>();
  private listeningPort: number | null = null;

  public constructor(private readonly options: ExtensionEgressProxyOptions) {
    this.server = createServer();
    this.server.on("connection", (socket) => this.track(socket));
    this.server.on("connect", (request, socket, head) => {
      void this.handleConnect(request, socket, head);
    });
    this.server.on("request", (request, response) => {
      void this.handleHttp(request, response);
    });
  }

  public get port(): number {
    if (this.listeningPort === null) throw new Error("The egress proxy is not listening.");
    return this.listeningPort;
  }

  public async start(): Promise<number> {
    await new Promise<void>((resolve, reject) => {
      this.server.once("error", reject);
      this.server.listen(0, "127.0.0.1", () => {
        this.server.off("error", reject);
        resolve();
      });
    });
    const address = this.server.address();
    if (address === null || typeof address === "string")
      throw new Error("Unexpected proxy address.");
    this.listeningPort = address.port;
    return address.port;
  }

  /** 停止监听并关闭全部连接与隧道。Stops listening and closes every connection and tunnel. */
  public async close(): Promise<void> {
    const closed = new Promise<void>((resolve) => this.server.close(() => resolve()));
    for (const socket of this.sockets) socket.destroy();
    this.sockets.clear();
    await closed;
  }

  private track(socket: Duplex): void {
    this.sockets.add(socket);
    socket.once("close", () => this.sockets.delete(socket));
  }

  private async resolveUpstream(url: string, blockedUrl: string, resourceType: "connect" | "http") {
    const upstream = parseResolveProxyResult(await this.options.resolveUpstream(url));
    if (upstream.type === "unsupported") {
      this.options.onBlocked({
        url: blockedUrl,
        resourceType,
        reason: "upstream-proxy-unsupported",
      });
      return null;
    }
    return upstream;
  }

  private async handleConnect(
    request: IncomingMessage,
    client: Duplex,
    head: Buffer,
  ): Promise<void> {
    this.track(client);
    client.on("error", () => client.destroy());
    const target = splitHostPort(request.url ?? "");
    if (!target) {
      client.end("HTTP/1.1 400 Bad Request\r\n\r\n");
      return;
    }
    const blockedUrl = `${target.host}:${target.port}`;
    if (!isConnectAllowed(this.options.getPermissions(), target.host, target.port)) {
      this.options.onBlocked({
        url: blockedUrl,
        resourceType: "connect",
        reason: "host-not-permitted",
      });
      client.end("HTTP/1.1 403 Forbidden\r\n\r\n");
      return;
    }
    const upstream = await this.resolveUpstream(`https://${blockedUrl}/`, blockedUrl, "connect");
    if (!upstream) {
      client.end("HTTP/1.1 403 Forbidden\r\n\r\n");
      return;
    }
    let tunnel: Duplex;
    try {
      tunnel = await openTunnel(upstream, target.host, target.port);
    } catch {
      client.end("HTTP/1.1 502 Bad Gateway\r\n\r\n");
      return;
    }
    if (client.destroyed) {
      tunnel.destroy();
      return;
    }
    this.track(tunnel);
    tunnel.on("error", () => client.destroy());
    client.on("close", () => tunnel.destroy());
    tunnel.on("close", () => client.destroy());
    client.write("HTTP/1.1 200 Connection Established\r\n\r\n");
    if (head.length > 0) tunnel.write(head);
    tunnel.pipe(client);
    client.pipe(tunnel);
  }

  private async handleHttp(request: IncomingMessage, response: ServerResponse): Promise<void> {
    let url: URL;
    try {
      url = new URL(request.url ?? "");
    } catch {
      response.writeHead(400).end();
      return;
    }
    if (url.protocol !== "http:") {
      response.writeHead(400).end();
      return;
    }
    if (!isProxyHttpRequestAllowed(this.options.getPermissions(), url.toString())) {
      this.options.onBlocked({
        url: url.toString(),
        resourceType: "http",
        reason: "host-not-permitted",
      });
      response.writeHead(403).end();
      return;
    }
    const upstream = await this.resolveUpstream(url.toString(), url.toString(), "http");
    if (!upstream) {
      response.writeHead(403).end();
      return;
    }
    const headers = { ...request.headers };
    for (const name of HOP_BY_HOP_HEADERS) delete headers[name];
    const port = url.port === "" ? 80 : Number(url.port);
    const forwarded = httpRequest({
      host: url.hostname,
      port,
      method: request.method,
      path: `${url.pathname}${url.search}`,
      headers,
      createConnection: (_options, created) => {
        openTunnel(upstream, url.hostname, port).then(
          (socket) => {
            this.track(socket);
            created(null, socket);
          },
          (error: Error) => created(error, undefined as never),
        );
        return undefined;
      },
    });
    forwarded.once("response", (upstreamResponse) => {
      response.writeHead(upstreamResponse.statusCode ?? 502, upstreamResponse.headers);
      upstreamResponse.pipe(response);
    });
    forwarded.once("error", () => {
      if (!response.headersSent) response.writeHead(502);
      response.end();
    });
    response.once("close", () => forwarded.destroy());
    request.pipe(forwarded);
  }
}
