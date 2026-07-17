import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const ENABLE_TERM = /\benable\b|>\s*(?:Enable|Enabled|Disabled)\s*</;
const PACKAGES_URL = new URL("../../../", import.meta.url);

async function assertFilesDoNotExposeEnable(files: URL[]) {
  for (const file of files) {
    const source = await readFile(file, "utf8");
    assert.doesNotMatch(source, ENABLE_TERM, `${file.pathname} should not expose Enable`);
  }
}

test("Project frontend does not expose enable state", async () => {
  await assertFilesDoNotExposeEnable([
    new URL("projects/src/ui-web/pages/projects/page.tsx", PACKAGES_URL),
    new URL(
      "projects/src/ui-web/pages/projects/components/create-project-dialog.tsx",
      PACKAGES_URL,
    ),
    new URL("projects/src/ui-web/pages/projects/components/edit-project-dialog.tsx", PACKAGES_URL),
    new URL("projects/src/ui-web/pages/projects/components/project-form-fields.tsx", PACKAGES_URL),
    new URL("projects/src/ui-web/pages/projects/details/page.tsx", PACKAGES_URL),
    new URL("projects/src/ui-web/pages/projects/details/project-details.ts", PACKAGES_URL),
    new URL("chat/src/ui-web/pages/chat/page.tsx", PACKAGES_URL),
  ]);
});

test("Agentflow frontend does not expose enable state", async () => {
  await assertFilesDoNotExposeEnable([
    new URL("agents/src/types/agentflow.ts", PACKAGES_URL),
    new URL("agents/src/ui-web/pages/agentflows/page.tsx", PACKAGES_URL),
    new URL("agents/src/ui-web/pages/agentflows/components/agentflows-table.tsx", PACKAGES_URL),
    new URL(
      "agents/src/ui-web/pages/agentflows/components/visual-agentflow-builder.tsx",
      PACKAGES_URL,
    ),
    new URL("api/src/lib/chat-target-options.ts", PACKAGES_URL),
  ]);
});
