import assert from "node:assert/strict";
import test from "node:test";

const MODULE_URL = new URL("./app-selector.ts", import.meta.url);

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

test("buildSelectedAppItems preserves selected IDs when App options are unavailable", async () => {
  const { buildSelectedAppItems } = await importSelectedOptionItemsModule();

  assert.deepEqual(
    buildSelectedAppItems(
      ["app-known", "app-missing"],
      [
        {
          id: "app-known",
          appName: "github",
          displayName: "GitHub",
          provider: "GitHub OAuth App",
          clientId: "gh-client",
          isAuthorized: true,
          isAuthorizationExpired: false,
          authorizationSubject: "octocat",
        },
      ],
    ),
    [
      {
        id: "app-known",
        title: "GitHub · gh-client",
        description: "GitHub OAuth App · Authorized",
      },
      {
        id: "app-missing",
        title: "app-missing",
        description: "App connection unavailable",
      },
    ],
  );
});
