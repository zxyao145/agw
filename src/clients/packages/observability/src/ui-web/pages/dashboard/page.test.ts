import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("dashboard renders TraceTable below its statistics content", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /import \{ TraceTable \} from "\.\/components\/trace-table";/);

  const statisticsErrorIndex = source.indexOf("statsQuery.isError");
  const traceTableIndex = source.indexOf("<TraceTable />");

  assert.notEqual(statisticsErrorIndex, -1);
  assert.ok(traceTableIndex > statisticsErrorIndex);
});

test("dashboard summary maps and formats all token usage totals", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /usageInputTokenCount: number;/);
  assert.match(source, /usageOutputTokenCount: number;/);
  assert.match(source, /usageTotalTokenCount: number;/);
  assert.match(
    source,
    /label: "Total Input Token",[\s\S]*?formatStat\(stats\?\.usageInputTokenCount, hasData\)/,
  );
  assert.match(
    source,
    /label: "Total Output Token",[\s\S]*?formatStat\(stats\?\.usageOutputTokenCount, hasData\)/,
  );
  assert.match(
    source,
    /label: "Total Token",[\s\S]*?formatStat\(stats\?\.usageTotalTokenCount, hasData\)/,
  );
  assert.match(source, /return value\.toLocaleString\(\);/);
});
