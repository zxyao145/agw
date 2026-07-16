import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

type PageCase = {
  name: string;
  page: URL;
  table?: URL;
  endpoint: string;
};

const PAGE_CASES: readonly PageCase[] = [
  {
    name: "agents",
    page: new URL("../app/(app)/(agents)/agents/page.tsx", import.meta.url),
    table: new URL("../app/(app)/(agents)/agents/components/agents-table.tsx", import.meta.url),
    endpoint: "/api/agents/paged",
  },
  {
    name: "agentflows",
    page: new URL("../app/(app)/(agents)/agentflows/page.tsx", import.meta.url),
    table: new URL(
      "../app/(app)/(agents)/agentflows/components/agentflows-table.tsx",
      import.meta.url,
    ),
    endpoint: "/api/agentflows/paged",
  },
  {
    name: "MCP tool servers",
    page: new URL("../app/(app)/(tools)/mcp-tool-servers/page.tsx", import.meta.url),
    endpoint: "/api/mcp-tool-servers/paged",
  },
  {
    name: "skills",
    page: new URL("../app/(app)/skills/page.tsx", import.meta.url),
    endpoint: "/api/skills/paged",
  },
];

for (const pageCase of PAGE_CASES) {
  test(`${pageCase.name} page uses the shared paged query and controls`, async () => {
    const source = await readFile(pageCase.page, "utf8");

    assert.match(source, /const \[pageIndex, setPageIndex\] = React\.useState\(1\)/);
    assert.match(source, /const \[pageSize, setPageSize\] = React\.useState\(DEFAULT_PAGE_SIZE\)/);
    assert.ok(source.includes(`apiGet("${pageCase.endpoint}"`));
    assert.match(source, /query: \{ pageIndex, pageSize \}/);
    assert.match(source, /placeholderData: keepPreviousData/);
    assert.match(source, /<TablePagination/);
  });

  test(`${pageCase.name} table displays effective update time`, async () => {
    const source = await readFile(pageCase.table ?? pageCase.page, "utf8");

    assert.match(source, /<TableHead[^>]*>Updated<\/TableHead>/);
    assert.match(source, /updateTime \?\? [a-zA-Z]+\.createTime/);
  });
}

test("agentflow options load from the full endpoint only while the visual dialog is open", async () => {
  const source = await readFile(PAGE_CASES[1].page, "utf8");

  assert.match(source, /queryKey: \["agentflows", "options"\]/);
  assert.match(source, /apiGet\("\/api\/agentflows"\)/);
  assert.match(source, /enabled: visualOpen/);
});
