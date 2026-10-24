import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import {
  CONFIRMATION_CSP,
  CONFIRMATION_PAGE_URL,
  createConfirmationProtocolHandler,
} from "./confirmation-protocol";

async function createHandler() {
  const scratch = await createScratchDirectory("confirmation-protocol");
  await mkdir(scratch.path, { recursive: true });
  await writeFile(join(scratch.path, "confirmation.js"), "console.log('page');");
  await writeFile(join(scratch.path, "confirmation.css"), "body { margin: 0; }");
  return { handler: createConfirmationProtocolHandler(scratch.path), dispose: scratch.dispose };
}

test("the page, its script and its styles are served with the CSP", async () => {
  const { handler, dispose } = await createHandler();
  try {
    const page = await handler(new Request(CONFIRMATION_PAGE_URL));
    assert.equal(page.status, 200);
    assert.equal(page.headers.get("Content-Security-Policy"), CONFIRMATION_CSP);
    assert.equal(page.headers.get("X-Content-Type-Options"), "nosniff");
    assert.match(await page.text(), /<script defer src="\/confirmation\.js"><\/script>/u);
    assert.equal(
      CONFIRMATION_CSP,
      "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'none'; img-src 'none'; frame-ancestors 'none'",
    );

    const script = await handler(new Request("agw-confirmation://app/confirmation.js"));
    assert.equal(script.headers.get("Content-Type"), "text/javascript; charset=utf-8");
    assert.equal(script.headers.get("Content-Security-Policy"), CONFIRMATION_CSP);
    assert.equal(await script.text(), "console.log('page');");
    const styles = await handler(new Request("agw-confirmation://app/confirmation.css"));
    assert.equal(styles.headers.get("Content-Type"), "text/css; charset=utf-8");
  } finally {
    await dispose();
  }
});

test("other paths, origins and methods are not found", async () => {
  const { handler, dispose } = await createHandler();
  try {
    for (const request of [
      new Request("agw-confirmation://app/other.js"),
      new Request("agw-confirmation://app/../secret.txt"),
      new Request("agw-confirmation://other/index.html"),
      new Request(CONFIRMATION_PAGE_URL, { method: "POST", body: "x" }),
    ]) {
      assert.equal((await handler(request)).status, 404, request.url);
    }
  } finally {
    await dispose();
  }
});
