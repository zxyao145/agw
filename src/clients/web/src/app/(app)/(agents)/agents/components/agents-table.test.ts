import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const AGENTS_TABLE_URL = new URL("./agents-table.tsx", import.meta.url);

test("AgentsTable imports Tooltip components from the shared ui package", async () => {
  const source = await readFile(AGENTS_TABLE_URL, "utf8");

  assert.match(
    source,
    /import \{ Tooltip, TooltipContent, TooltipTrigger \} from "@\/components\/ui\/tooltip";/,
  );
});

test("AgentsTable wraps the Instructions cell in a Tooltip when systemPrompt is non-empty", async () => {
  const source = await readFile(AGENTS_TABLE_URL, "utf8");

  assert.match(source, /<TableCell className="max-w-xs">\s*\{agent\.systemPrompt \? \(/);
  assert.match(source, /<Tooltip>\s*<TooltipTrigger asChild>/);
  assert.match(
    source,
    /<span className="block truncate text-xs" tabIndex=\{0\}>\s*\{agent\.systemPrompt\}\s*<\/span>/,
  );
  assert.match(
    source,
    /<TooltipContent[\s\S]*?whitespace-pre-wrap break-words text-left"[\s\S]*?>\s*\{agent\.systemPrompt\}\s*<\/TooltipContent>/,
  );
});

test("AgentsTable renders a muted fallback when systemPrompt is empty", async () => {
  const source = await readFile(AGENTS_TABLE_URL, "utf8");

  assert.match(
    source,
    /\{agent\.systemPrompt \? \([\s\S]*?\) : \(\s*<span className="text-muted-foreground">-<\/span>\s*\)\s*\}\s*<\/TableCell>/,
  );
});
