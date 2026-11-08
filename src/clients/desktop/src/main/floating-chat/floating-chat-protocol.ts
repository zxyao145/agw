import { readFile } from "node:fs/promises";
import { join } from "node:path";

export const FLOATING_CHAT_SCHEME = "agw-floating-chat";
export const FLOATING_CHAT_ORIGIN = `${FLOATING_CHAT_SCHEME}://app`;
export const FLOATING_CHAT_PAGE_URL = `${FLOATING_CHAT_ORIGIN}/index.html`;

export const FLOATING_CHAT_SCHEME_PRIVILEGES = {
  scheme: FLOATING_CHAT_SCHEME,
  privileges: { standard: true, secure: true },
};

export const FLOATING_CHAT_CSP = [
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
    <title>Agw Desktop Floating Chat</title>
    <link rel="stylesheet" href="/floating-chat.css" />
    <script defer src="/floating-chat.js"></script>
  </head>
  <body></body>
</html>
`;

const FILES: Readonly<Record<string, string>> = {
  "/floating-chat.js": "text/javascript; charset=utf-8",
  "/floating-chat.css": "text/css; charset=utf-8",
};

function respond(body: BodyInit, contentType: string): Response {
  return new Response(body, {
    status: 200,
    headers: {
      "Content-Type": contentType,
      "Content-Security-Policy": FLOATING_CHAT_CSP,
      "X-Content-Type-Options": "nosniff",
      "Cache-Control": "no-store",
    },
  });
}

export function createFloatingChatProtocolHandler(
  directory: string,
): (request: Request) => Promise<Response> {
  return async (request) => {
    const url = new URL(request.url);
    if (
      url.protocol !== `${FLOATING_CHAT_SCHEME}:` ||
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
