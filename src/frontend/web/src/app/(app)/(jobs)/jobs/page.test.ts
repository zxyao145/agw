import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("job details dialog keeps header and footer fixed while details body scrolls", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(
    source,
    /<DialogContent size="2xl" className="flex max-h-\[90vh\] flex-col overflow-hidden">/,
  );
  assert.match(source, /<div className="min-h-0 flex-1 space-y-6 overflow-y-auto pr-1">/);
});
