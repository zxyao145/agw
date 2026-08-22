import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const HISTORY_URL = new URL("./conversation-history.tsx", import.meta.url);
const COMPOSER_URL = new URL("./composer.tsx", import.meta.url);
const WORKSPACE_URL = new URL("./native-workspace-provider.tsx", import.meta.url);

test("native renderer consumes the shared render-item union and native markdown", async () => {
  const source = await readFile(HISTORY_URL, "utf8");
  assert.match(source, /type ConversationRenderItem/);
  assert.match(source, /EnrichedMarkdownText/);
  assert.match(source, /flavor="github"/);
  assert.match(source, /messageRowAgent: \{ width: "88%"/);
  assert.match(source, /messageRowUser: \{ width: "88%"/);
  assert.match(source, /toolCard:\s*\{[\s\S]*?width: "100%"/);
  assert.match(source, /containerStyle=\{markdownContainerStyle\}/);
  assert.match(source, /markdownContainerStyle = \{[\s\S]*?width: "100%"/);
  assert.match(source, /updateAutoScrollState/);
  assert.doesNotMatch(source, /processMessages|collapseConsecutiveSystemMessages/);
});

test("native Chat vertical slice has no application alias imports", async () => {
  const sources = await Promise.all(
    [HISTORY_URL, COMPOSER_URL, WORKSPACE_URL].map((url) => readFile(url, "utf8")),
  );
  for (const source of sources) {
    assert.doesNotMatch(source, /["']@\//);
    assert.doesNotMatch(source, /mobile\/src|@agw\/components|@agw\/chat["']/);
  }
});
