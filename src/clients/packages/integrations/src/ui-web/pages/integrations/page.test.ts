import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("integrations page renders configured integrations above available integrations", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /<ConnectionCard/);
  assert.match(source, /<h2 className="text-xl font-semibold">Configured integrations<\/h2>/);
  assert.match(source, /<PluginCard/);
  assert.match(source, /canConfigureInstallation=\{canConfigureInstallations\}/);
  assert.match(source, /authSessionQuery\.data\?\.userId === ADMIN_USER_ID/);
  assert.match(source, /queryFn: getAuthSession/);
  assert.match(source, /Available integrations/);
  assert.match(source, /<ConnectionDialog/);
  assert.match(source, /<PluginInstallationDialog/);
  assert.match(source, /callbackUrl=\{callbackUrl\}/);
  assert.match(source, /\/api\/integrations\/oauth\/callback-info/);
  assert.match(source, /\/api\/integrations\/oauth\/authorize-start/);
  assert.match(source, /completionTarget/);
  assert.match(source, /alias: createDefaultConnectionAlias\(selection\.plugin\.id\)/);
  assert.doesNotMatch(source, /app-instances|app-definitions/);
  assert.doesNotMatch(source, /_account/);
});
