import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const SOURCE_ROOT = fileURLToPath(new URL("../", import.meta.url));
const PACKAGES_URL = new URL("../../../", import.meta.url);

const forbiddenDateFormatters = [
  /\.toLocaleDateString\s*\(/,
  /\.toLocaleTimeString\s*\(/,
  /new\s+Intl\.DateTimeFormat\s*\(/,
  /new\s+Date\([^)]*\)\.toLocaleString\s*\(/,
  /\b(?:date|d|parsedDate|timestamp)\.toLocaleString\s*\(/,
];

async function collectProductionTypeScriptFiles(directory: string): Promise<string[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = await Promise.all(
    entries.map(async (entry) => {
      const entryPath = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        return collectProductionTypeScriptFiles(entryPath);
      }

      const isTypeScript = entry.name.endsWith(".ts") || entry.name.endsWith(".tsx");
      const isExcluded = entry.name.endsWith(".test.ts") || entryPath.endsWith("api/openapi.d.ts");
      return isTypeScript && !isExcluded ? [entryPath] : [];
    }),
  );

  return files.flat();
}

test("browser date displays do not use locale-dependent date formatters", async () => {
  const files = await collectProductionTypeScriptFiles(SOURCE_ROOT);
  const violations: string[] = [];

  for (const file of files) {
    const source = await readFile(file, "utf8");
    for (const pattern of forbiddenDateFormatters) {
      if (pattern.test(source)) {
        violations.push(`${path.relative(SOURCE_ROOT, file)}: ${pattern.source}`);
      }
    }
  }

  assert.deepEqual(violations, []);
});

test("special date display sites use the shared local formatters", async () => {
  const [commentSource, connectionSource, jobsSource] = await Promise.all([
    readFile(
      new URL("projects/src/ui-web/components/file-explorer/comment-section.tsx", PACKAGES_URL),
      "utf8",
    ),
    readFile(
      new URL(
        "integrations/src/ui-web/pages/integrations/components/connection-card.tsx",
        PACKAGES_URL,
      ),
      "utf8",
    ),
    readFile(new URL("jobs/src/ui-web/pages/jobs/page.tsx", PACKAGES_URL), "utf8"),
  ]);

  assert.match(commentSource, /formatLocalDateTimeExact\(comment\.timestamp\)/);
  assert.match(connectionSource, /formatLocalDateTime\(connection\.expiresAtUtc\)/);

  const onceTriggerDisplays = jobsSource.match(
    /job\.triggerType === TRIGGER_TYPE_ONCE[\s\S]{0,160}formatLocalDateTime\(job\.triggerValue\)/g,
  );
  assert.equal(onceTriggerDisplays?.length, 2);
});
