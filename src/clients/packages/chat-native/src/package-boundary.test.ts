import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const HISTORY_URL = new URL("./conversation-history.tsx", import.meta.url);
const COMPOSER_URL = new URL("./composer.tsx", import.meta.url);
const WORKSPACE_URL = new URL("./native-workspace-provider.tsx", import.meta.url);
const THEME_URL = new URL("./theme.ts", import.meta.url);

test("native renderer consumes the shared render-item union and native markdown", async () => {
  const source = await readFile(HISTORY_URL, "utf8");
  assert.match(source, /type ConversationRenderItem/);
  assert.match(source, /EnrichedMarkdownText/);
  assert.match(source, /flavor="github"/);
  assert.match(source, /messageRowAgent: \{ width: "88%"/);
  assert.match(source, /messageRowUser: \{ maxWidth: "88%"/);
  assert.match(source, /maximumUserBubbleWidth/);
  assert.match(source, /onTextLayout=\{handleUserTextLayout\}/);
  assert.match(source, /toolCard:\s*\{[\s\S]*?width: "100%"/);
  assert.match(source, /resultSection:\s*\{[\s\S]*?borderStyle: "dashed"/);
  assert.match(source, /resultBubble:\s*\{[\s\S]*?backgroundColor: theme\.white/);
  assert.match(source, /resultHeading:\s*\{[\s\S]*?borderStyle: "dashed"/);
  assert.match(source, /containerStyle=\{markdownContainerStyle\}/);
  assert.match(source, /markdownContainerStyle = \{[\s\S]*?width: "100%"/);
  assert.match(source, /updateAutoScrollState/);
  assert.doesNotMatch(source, /processMessages|collapseConsecutiveSystemMessages/);
});

test("native markdown uses the borderless Web code style", async () => {
  const [historySource, themeSource] = await Promise.all([
    readFile(HISTORY_URL, "utf8"),
    readFile(THEME_URL, "utf8"),
  ]);
  const inlineCodeStyle = /code:\s*\{([^}]*)\}/.exec(historySource)?.[1];
  const codeBlockStyle = /codeBlock:\s*\{([\s\S]*?)\n\s*\},\n\s*table:/.exec(historySource)?.[1];
  assert.ok(inlineCodeStyle);
  assert.ok(codeBlockStyle);
  assert.match(inlineCodeStyle, /borderColor: "transparent"/);
  assert.match(codeBlockStyle, /borderColor: "transparent"/);
  assert.match(codeBlockStyle, /borderWidth: 0/);
  assert.match(historySource, /codeBlock:\s*\{[\s\S]*?backgroundColor: theme\.code/);
  assert.match(historySource, /padInlineCode\(normalizeMathDelimiters\(value\)\)/);
  assert.match(themeSource, /code: "#f4f4f4"/);
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
