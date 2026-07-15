import assert from "node:assert/strict";
import test from "node:test";

const MODULE_URL = new URL("./selection-items.ts", import.meta.url);

async function importSelectedOptionItemsModule() {
  try {
    return await import(MODULE_URL.href);
  } catch (error) {
    assert.fail(`selected option item helpers are missing or invalid: ${String(error)}`);
  }
}

test("buildSelectedSkillItems preserves selected IDs when Skill options are unavailable", async () => {
  const { buildSelectedSkillItems } = await importSelectedOptionItemsModule();

  assert.deepEqual(
    buildSelectedSkillItems(
      ["skill-known", "skill-missing"],
      [
        {
          id: "skill-known",
          name: "Known skill",
          description: "Available",
          agentIds: [],
        },
      ],
    ),
    [
      { id: "skill-known", title: "Known skill", description: "Available" },
      { id: "skill-missing", title: "skill-missing", description: "Skill unavailable" },
    ],
  );
});
