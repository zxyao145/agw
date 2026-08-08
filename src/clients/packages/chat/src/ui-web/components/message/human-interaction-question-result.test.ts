import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const RESULT_VIEW_URL = new URL("./human-interaction-question-result.tsx", import.meta.url);

test("completed question interaction renders each question before its plain-text answer", async () => {
  const source = await readFile(RESULT_VIEW_URL, "utf8");

  assert.match(source, /Asked \{questionCount\}/);
  assert.match(source, /expanded \? \([\s\S]*?<ChevronUp[\s\S]*?: \([\s\S]*?<ChevronDown/);
  assert.match(source, /<ChevronDown[\s\S]*?Asked \{questionCount\}[\s\S]*?<CircleHelp/);
  assert.match(source, /\{item\.question\}[\s\S]*?item\.answer/);
  assert.doesNotMatch(source, /JSON\.stringify|AiMessageComponent|<pre/);
});
