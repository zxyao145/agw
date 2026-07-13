import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const TRACE_TABLE_URL = new URL("./trace-table.tsx", import.meta.url);
const ROOT_LAYOUT_URL = new URL("../../../../layout.tsx", import.meta.url);

test("Error cell uses shadcn Tooltip for complete hover and focus content", async () => {
  const source = await readFile(TRACE_TABLE_URL, "utf8");

  assert.match(source, /from "@\/components\/ui\/tooltip";/);
  assert.match(source, /<Tooltip>/);
  assert.match(source, /<TooltipTrigger asChild>/);
  assert.match(source, /tabIndex=\{0\}/);
  assert.match(source, /<TooltipContent[\s\S]*?\{trace\.error\}[\s\S]*?<\/TooltipContent>/);
  assert.doesNotMatch(source, /title=\{trace\.error/);
});

test("root layout provides shadcn Tooltip context", async () => {
  const source = await readFile(ROOT_LAYOUT_URL, "utf8");

  assert.match(source, /import \{ TooltipProvider \} from "@\/components\/ui\/tooltip";/);
  assert.match(source, /<TooltipProvider>[\s\S]*?<QueryProvider>/);
  assert.match(source, /<\/QueryProvider>[\s\S]*?<\/TooltipProvider>/);
});
