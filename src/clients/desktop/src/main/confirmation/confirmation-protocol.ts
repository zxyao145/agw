import { readFile } from "node:fs/promises";
import { join } from "node:path";

export const CONFIRMATION_SCHEME = "agw-confirmation";
export const CONFIRMATION_ORIGIN = `${CONFIRMATION_SCHEME}://app`;
export const CONFIRMATION_PAGE_URL = `${CONFIRMATION_ORIGIN}/index.html`;

/** `protocol.registerSchemesAsPrivileged` 中的确认页面入口。The confirmation page entry for `protocol.registerSchemesAsPrivileged`. */
export const CONFIRMATION_SCHEME_PRIVILEGES = {
  scheme: CONFIRMATION_SCHEME,
  privileges: { standard: true, secure: true },
};

/** 确认页面只加载自己的脚本与样式。The confirmation page loads only its own script and styles. */
export const CONFIRMATION_CSP = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self'",
  "connect-src 'none'",
  "img-src 'none'",
  "frame-ancestors 'none'",
].join("; ");

const PAGE_HTML = `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>Agw Desktop</title>
    <link rel="stylesheet" href="/confirmation.css" />
    <script defer src="/confirmation.js"></script>
  </head>
  <body>
    <main id="confirmation"></main>
  </body>
</html>
`;

const FILES: Readonly<Record<string, string>> = {
  "/confirmation.js": "text/javascript; charset=utf-8",
  "/confirmation.css": "text/css; charset=utf-8",
};

function respond(body: BodyInit, contentType: string): Response {
  return new Response(body, {
    status: 200,
    headers: {
      "Content-Type": contentType,
      "Content-Security-Policy": CONFIRMATION_CSP,
      "X-Content-Type-Options": "nosniff",
      "Cache-Control": "no-store",
    },
  });
}

/**
 * 确认页面的协议处理函数：只提供页面、`/confirmation.js` 与 `/confirmation.css`（来自 `directory`），每个响应都带 CSP。
 * The confirmation page's protocol handler: serves only the page, `/confirmation.js` and `/confirmation.css` (from
 * `directory`), each response with the CSP.
 */
export function createConfirmationProtocolHandler(
  directory: string,
): (request: Request) => Promise<Response> {
  return async (request) => {
    // Node 的 URL 不知道这个协议是标准协议，origin 为 "null"，因此比较 protocol 与 host。
    // Node's URL does not know the scheme is standard and reports a "null" origin, so compare protocol and host.
    const url = new URL(request.url);
    if (
      url.protocol !== `${CONFIRMATION_SCHEME}:` ||
      url.host !== "app" ||
      request.method !== "GET"
    ) {
      return new Response(null, { status: 404 });
    }
    if (url.pathname === "/" || url.pathname === "/index.html") {
      return respond(PAGE_HTML, "text/html; charset=utf-8");
    }
    const contentType = FILES[url.pathname];
    if (contentType === undefined) return new Response(null, { status: 404 });
    return respond(await readFile(join(directory, url.pathname.slice(1))), contentType);
  };
}
