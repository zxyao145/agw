import { realpath, stat } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { lookup } from "mime-types";

import type { ExtensionManifest } from "@agw/extension-sdk";
import { isValidPackagePath } from "../../shared/extension-manifest";
import { isPathInside } from "./extension-catalog";
import { buildExtensionCsp } from "./extension-csp";

/** 协议处理函数读取的 extension 当前状态。The extension's current state read by the protocol handler. */
export type ExtensionProtocolState = {
  manifest: ExtensionManifest;
  rootDirectory: string;
  /** 已启用且校验通过。Enabled and valid. */
  enabled: boolean;
  grantedHostPermissions: readonly string[];
};

export type ExtensionProtocolOptions = {
  extensionId: string;
  /** manifest 校验通过时返回状态，否则为 null。Returns the state when the manifest is valid, otherwise null. */
  getState(): ExtensionProtocolState | null;
  /** `resources/extension-sdk`。 */
  sdkDirectory: string;
  /** 生产环境为 `net.fetch`。`net.fetch` in production. */
  fetchFile(fileUrl: string): Promise<Response>;
};

/**
 * `protocol.registerSchemesAsPrivileged` 中的 `agw-extension` 项；不设置 bypassCSP、allowServiceWorkers、corsEnabled。
 * The `agw-extension` entry for `protocol.registerSchemesAsPrivileged`, without bypassCSP, allowServiceWorkers or corsEnabled.
 */
export const EXTENSION_SCHEME_PRIVILEGES = {
  scheme: "agw-extension",
  privileges: {
    standard: true,
    secure: true,
    supportFetchAPI: true,
    stream: true,
    codeCache: true,
  },
};

const RESERVED_PREFIX = "__agw/";

function contentType(path: string): string {
  if (path.endsWith(".wasm")) return "application/wasm";
  if (path.endsWith(".js") || path.endsWith(".mjs")) return "text/javascript";
  if (path.endsWith(".d.ts")) return "text/plain; charset=utf-8";
  return lookup(path) || "application/octet-stream";
}

function notFound(): Response {
  return new Response(null, { status: 404 });
}

/** 创建绑定一个 extension 的 `agw-extension` 处理函数。Creates an `agw-extension` handler bound to one extension. */
export function createExtensionProtocolHandler(
  options: ExtensionProtocolOptions,
): (request: Request) => Promise<Response> {
  const respond = (state: ExtensionProtocolState, body: BodyInit | null, path: string) =>
    new Response(body, {
      status: 200,
      headers: {
        "Content-Type": contentType(path),
        "Content-Security-Policy": buildExtensionCsp(state.grantedHostPermissions),
        "X-Content-Type-Options": "nosniff",
        "Cache-Control": "no-cache",
      },
    });

  const serveFile = async (state: ExtensionProtocolState, file: string, path: string) => {
    const source = await options.fetchFile(pathToFileURL(file).toString());
    if (!source.ok) return notFound();
    return respond(state, source.body, path);
  };

  return async (request) => {
    const url = new URL(request.url);
    if (url.host !== options.extensionId) return notFound();
    const state = options.getState();
    if (!state) return notFound();

    let path: string;
    try {
      path = decodeURIComponent(url.pathname.slice(1));
    } catch {
      return notFound();
    }

    if (path.startsWith(RESERVED_PREFIX)) {
      const name = path.slice(RESERVED_PREFIX.length);
      if (name === "background.html") {
        if (state.manifest.background === undefined) return notFound();
        return respond(
          state,
          `<!doctype html><meta charset="utf-8"><script type="module" src="/${state.manifest.background}"></script>`,
          "background.html",
        );
      }
      if (
        name !== "sdk.js" &&
        name !== "sdk.d.ts" &&
        name !== "manifest.schema.json" &&
        name !== "typography.css"
      ) {
        return notFound();
      }
      const file = join(options.sdkDirectory, `v${state.manifest.manifestVersion}`, name);
      return serveFile(state, file, name);
    }

    if (!state.enabled || !isValidPackagePath(path)) return notFound();
    let rootRealPath: string;
    let fileRealPath: string;
    try {
      rootRealPath = await realpath(state.rootDirectory);
      fileRealPath = await realpath(join(rootRealPath, ...path.split("/")));
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code === "ENOENT" || code === "ENOTDIR" || code === "ELOOP") return notFound();
      throw error;
    }
    if (!isPathInside(rootRealPath, fileRealPath)) return notFound();
    if (!(await stat(fileRealPath)).isFile()) return notFound();
    return serveFile(state, fileRealPath, path);
  };
}
