import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("agents page loads integration app instances and passes app selection state into both dialogs", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /queryKey: \["appInstances"\]/);
  assert.match(source, /apiGet\("\/api\/integrations\/app-instances"\)/);
  assert.match(source, /selectedAppInstanceIds/);
  assert.match(source, /toggleAppInstance/);
  assert.match(source, /selectedAppInstanceIds=\{selectedAppInstanceIds\}/);
  assert.match(source, /selectedAppInstanceIds=\{editSelectedAppInstanceIds\}/);
  assert.match(source, /setEditModelProviderId\(agent\.modelProviderId \?\? ""\)/);
});
