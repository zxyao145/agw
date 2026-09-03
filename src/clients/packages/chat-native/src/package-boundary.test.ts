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
  assert.match(source, /messageBlock: \{ width: "100%", gap: 4 \}/);
  assert.match(source, /messageRow: \{ width: "100%", flexDirection: "row" \}/);
  assert.match(source, /messageRowAgent: \{ justifyContent: "flex-start" \}/);
  assert.match(source, /messageRowUser: \{ justifyContent: "flex-end" \}/);
  assert.match(source, /bubble:\s*\{[\s\S]*?maxWidth: "88%"[\s\S]*?flexShrink: 1/);
  assert.match(source, /fullBubble: \{ width: "100%", maxWidth: "100%" \}/);
  assert.doesNotMatch(source, /messageRow(?:Agent|User): \{[^}]*alignSelf/);
  assert.doesNotMatch(source, /maximumUserBubbleWidth|userBubbleWidth|userMeasureText/);
  assert.doesNotMatch(source, /onTextLayout=\{handleUserTextLayout\}/);
  assert.match(source, /toolCard:\s*\{[\s\S]*?width: "100%"/);
  assert.match(source, /resultSection:\s*\{[\s\S]*?borderStyle: "dashed"/);
  assert.match(source, /resultBubble:\s*\{[\s\S]*?backgroundColor: theme\.white/);
  assert.match(source, /resultHeading:\s*\{[\s\S]*?borderStyle: "dashed"/);
  assert.doesNotMatch(source, /containerStyle=\{markdownContainerStyle\}/);
  assert.doesNotMatch(source, /const markdownContainerStyle/);
  assert.match(source, /updateAutoScrollState/);
  assert.match(source, /collapseToolRuns: false/);
  assert.match(
    source,
    /\[message\.meta\?\.name, message\.meta\?\.author, message\.meta\?\.model\]/,
  );
  assert.doesNotMatch(source, /processMessages|collapseConsecutiveSystemMessages/);
});

test("native reasoning markdown keeps its intrinsic width in compact agent bubbles", async () => {
  const source = await readFile(HISTORY_URL, "utf8");
  const collapsibleContentStyle = /collapsibleContent:\s*\{([^}]*)\}/.exec(source)?.[1];

  assert.ok(collapsibleContentStyle);
  assert.match(collapsibleContentStyle, /flexShrink:\s*1/);
  assert.doesNotMatch(collapsibleContentStyle, /\bflex:\s*1/);
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
