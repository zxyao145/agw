import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import {
  FLOATING_CHAT_CSP,
  FLOATING_CHAT_PAGE_URL,
  FLOATING_CHAT_SCHEME_PRIVILEGES,
  createFloatingChatProtocolHandler,
} from "./floating-chat-protocol";

async function createHandler() {
  const scratch = await createScratchDirectory("floating-chat-protocol");
  await mkdir(scratch.path, { recursive: true });
  await writeFile(join(scratch.path, "floating-chat.js"), "console.log('floating chat');");
  await writeFile(join(scratch.path, "floating-chat.css"), "body { margin: 0; }");
  return { handler: createFloatingChatProtocolHandler(scratch.path), dispose: scratch.dispose };
}

test("the floating Chat page resources are served from its dedicated privileged scheme", async () => {
  const { handler, dispose } = await createHandler();
  try {
    assert.deepEqual(FLOATING_CHAT_SCHEME_PRIVILEGES, {
      scheme: "agw-floating-chat",
      privileges: { standard: true, secure: true },
    });

    const page = await handler(new Request(FLOATING_CHAT_PAGE_URL));
    assert.equal(page.status, 200);
    assert.equal(page.headers.get("Content-Type"), "text/html; charset=utf-8");
    assert.equal(page.headers.get("Content-Security-Policy"), FLOATING_CHAT_CSP);
    assert.equal(page.headers.get("X-Content-Type-Options"), "nosniff");
    assert.match(await page.text(), /<script defer src="\/floating-chat\.js"><\/script>/u);

    const script = await handler(new Request("agw-floating-chat://app/floating-chat.js"));
    assert.equal(script.headers.get("Content-Type"), "text/javascript; charset=utf-8");
    assert.equal(script.headers.get("Content-Security-Policy"), FLOATING_CHAT_CSP);
    assert.equal(await script.text(), "console.log('floating chat');");

    const styles = await handler(new Request("agw-floating-chat://app/floating-chat.css"));
    assert.equal(styles.headers.get("Content-Type"), "text/css; charset=utf-8");
    assert.equal(await styles.text(), "body { margin: 0; }");
  } finally {
    await dispose();
  }
});

test("the floating Chat protocol rejects other paths, origins and methods", async () => {
  const { handler, dispose } = await createHandler();
  try {
    for (const request of [
      new Request("agw-floating-chat://app/other.js"),
      new Request("agw-floating-chat://app/../secret.txt"),
      new Request("agw-floating-chat://other/index.html"),
      new Request(FLOATING_CHAT_PAGE_URL, { method: "POST", body: "x" }),
    ]) {
      assert.equal((await handler(request)).status, 404, request.url);
    }
  } finally {
    await dispose();
  }
});
