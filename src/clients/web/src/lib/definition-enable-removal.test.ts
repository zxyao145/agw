import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const ENABLE_TERM = /\benable\b|>\s*(?:Enable|Enabled|Disabled)\s*</;

async function assertFilesDoNotExposeEnable(files: URL[]) {
  for (const file of files) {
    const source = await readFile(file, "utf8");
    assert.doesNotMatch(source, ENABLE_TERM, `${file.pathname} should not expose Enable`);
  }
}

test("Project frontend does not expose enable state", async () => {
  await assertFilesDoNotExposeEnable([
    new URL("../app/(app)/(tasks)/projects/page.tsx", import.meta.url),
    new URL("../app/(app)/(tasks)/projects/components/create-project-dialog.tsx", import.meta.url),
    new URL("../app/(app)/(tasks)/projects/components/edit-project-dialog.tsx", import.meta.url),
    new URL("../app/(app)/(tasks)/projects/components/project-form-fields.tsx", import.meta.url),
    new URL("../app/(app)/(tasks)/projects/details/page.tsx", import.meta.url),
    new URL("../app/(app)/(tasks)/projects/details/project-details.ts", import.meta.url),
    new URL("../app/(app)/(interface)/chat/page.tsx", import.meta.url),
  ]);
});

test("Agentflow frontend does not expose enable state", async () => {
  await assertFilesDoNotExposeEnable([
    new URL("../types/agentflow.ts", import.meta.url),
    new URL("../app/(app)/(agents)/agentflows/page.tsx", import.meta.url),
    new URL("../app/(app)/(agents)/agentflows/components/agentflows-table.tsx", import.meta.url),
    new URL(
      "../app/(app)/(agents)/agentflows/components/visual-agentflow-builder.tsx",
      import.meta.url,
    ),
    new URL("./chat-target-options.ts", import.meta.url),
  ]);
});
