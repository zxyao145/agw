import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PACKAGES_URL = new URL("../../../", import.meta.url);
const TABLE_URL = new URL("./shadcn/table.tsx", import.meta.url);
const TABLE_PAGINATION_URL = new URL("./table-pagination.tsx", import.meta.url);
const STATIC_TABLE_URL = new URL("./static-table/index.tsx", import.meta.url);
const PROJECTS_PAGE_URL = new URL("projects/src/ui-web/pages/projects/page.tsx", PACKAGES_URL);
const AGENTFLOWS_TABLE_URL = new URL(
  "agents/src/ui-web/pages/agentflows/components/agentflows-table.tsx",
  PACKAGES_URL,
);
const AGENTS_TABLE_URL = new URL(
  "agents/src/ui-web/pages/agents/components/agents-table.tsx",
  PACKAGES_URL,
);
const STATIC_TABLE_CONSUMERS = [
  ["Agents", AGENTS_TABLE_URL],
  [
    "Providers",
    new URL("providers/src/ui-web/pages/providers/components/providers-table.tsx", PACKAGES_URL),
  ],
  [
    "Models",
    new URL("providers/src/ui-web/pages/models/components/models-table.tsx", PACKAGES_URL),
  ],
  ["Skills", new URL("skills/src/ui-web/pages/skills/page.tsx", PACKAGES_URL)],
  [
    "MCP Tool Servers",
    new URL("integrations/src/ui-web/pages/mcp-tool-servers/page.tsx", PACKAGES_URL),
  ],
  ["Jobs", new URL("jobs/src/ui-web/pages/jobs/page.tsx", PACKAGES_URL)],
] as const;
const PAGED_MANAGEMENT_TABLES = [
  ["Agents", new URL("agents/src/ui-web/pages/agents/page.tsx", PACKAGES_URL), "AgentsTable"],
  [
    "Agentflows",
    new URL("agents/src/ui-web/pages/agentflows/page.tsx", PACKAGES_URL),
    "AgentflowsTable",
  ],
  ["Skills", new URL("skills/src/ui-web/pages/skills/page.tsx", PACKAGES_URL), "StaticTable"],
  [
    "MCP Tool Servers",
    new URL("integrations/src/ui-web/pages/mcp-tool-servers/page.tsx", PACKAGES_URL),
    "StaticTable",
  ],
] as const;

test("shared management tables and paginated surfaces render on card backgrounds", async () => {
  const [tableSource, paginationSource] = await Promise.all([
    readFile(TABLE_URL, "utf8"),
    readFile(TABLE_PAGINATION_URL, "utf8"),
  ]);

  assert.match(tableSource, /data-slot="table" className="[^"]*bg-card[^"]*"/);
  assert.match(paginationSource, /className="overflow-hidden rounded-md border bg-card"/);
});

test("management table borders clip card surfaces at the medium radius", async () => {
  const [staticTableSource, projectsSource, agentflowsSource, ...consumerSources] =
    await Promise.all([
      readFile(STATIC_TABLE_URL, "utf8"),
      readFile(PROJECTS_PAGE_URL, "utf8"),
      readFile(AGENTFLOWS_TABLE_URL, "utf8"),
      ...STATIC_TABLE_CONSUMERS.map(([, url]) => readFile(url, "utf8")),
    ]);
  const clippedMediumBorder = /"overflow-hidden rounded-md border"/;

  assert.match(staticTableSource, clippedMediumBorder);
  assert.match(agentflowsSource, clippedMediumBorder);
  assert.match(
    projectsSource,
    /<div className="overflow-hidden rounded-md border">\s*<Table(?:\s|>)/,
  );

  STATIC_TABLE_CONSUMERS.forEach(([pageName], index) => {
    assert.match(consumerSources[index], /<StaticTable/, `${pageName} must use StaticTable`);
  });
});

test("paged management tables and pagination share one rounded parent surface", async () => {
  const [
    paginationSource,
    staticTableSource,
    agentsTableSource,
    agentflowsTableSource,
    ...sources
  ] = await Promise.all([
    readFile(TABLE_PAGINATION_URL, "utf8"),
    readFile(STATIC_TABLE_URL, "utf8"),
    readFile(AGENTS_TABLE_URL, "utf8"),
    readFile(AGENTFLOWS_TABLE_URL, "utf8"),
    ...PAGED_MANAGEMENT_TABLES.map(([, url]) => readFile(url, "utf8")),
  ]);

  assert.match(paginationSource, /export function PaginatedTable/);
  assert.match(paginationSource, /className="overflow-hidden rounded-md border bg-card"/);
  assert.match(paginationSource, /className="[^"]*border-t bg-card[^"]*"/);
  assert.match(
    staticTableSource,
    /embedded \? "overflow-hidden" : "overflow-hidden rounded-md border"/,
  );
  assert.match(agentsTableSource, /<StaticTable embedded={embedded}/);
  assert.match(
    agentflowsTableSource,
    /embedded \? "overflow-hidden" : "overflow-hidden rounded-md border"/,
  );

  PAGED_MANAGEMENT_TABLES.forEach(([pageName, , tableComponent], index) => {
    const source = sources[index];
    const surfaceStart = source.indexOf("<PaginatedTable");
    const tableStart = source.indexOf(`<${tableComponent}`, surfaceStart);
    const embeddedProp = source.indexOf("embedded", tableStart);
    const surfaceEnd = source.indexOf("</PaginatedTable>", tableStart);

    assert.ok(surfaceStart >= 0, `${pageName} must use PaginatedTable`);
    assert.ok(tableStart > surfaceStart, `${pageName} table must be inside PaginatedTable`);
    assert.ok(
      embeddedProp > tableStart && embeddedProp < surfaceEnd,
      `${pageName} table must be embedded`,
    );
    assert.ok(surfaceEnd > tableStart, `${pageName} must close PaginatedTable after its table`);
    assert.doesNotMatch(
      source,
      /<TablePagination\b/,
      `${pageName} must not own pagination separately`,
    );
  });
});
