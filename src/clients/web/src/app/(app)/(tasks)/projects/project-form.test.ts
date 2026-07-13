import assert from "node:assert/strict";
import test from "node:test";

const PROJECT_FORM_MODULE_URL = new URL("./project-form.ts", import.meta.url);

async function importProjectFormModule() {
  try {
    return await import(PROJECT_FORM_MODULE_URL.href);
  } catch (error) {
    assert.fail(`project-form module is missing or invalid: ${String(error)}`);
  }
}

test("formatProjectFolderName replaces special characters with underscores", async () => {
  const { formatProjectFolderName } = await importProjectFormModule();

  assert.equal(formatProjectFolderName("  Demo Project: Alpha?  "), "Demo_Project_Alpha");
});

test("formatProjectFolderName returns an empty value when no valid folder name remains", async () => {
  const { formatProjectFolderName } = await importProjectFormModule();

  assert.equal(formatProjectFolderName(' <>:"/\\|?* '), "");
});

test("getDefaultProjectWorkspace builds the project workspace from the formatted name", async () => {
  const { getDefaultProjectWorkspace } = await importProjectFormModule();

  assert.equal(getDefaultProjectWorkspace("Demo Project: Alpha?"), "~/.agw/Demo_Project_Alpha");
});

test("syncDefaultProjectWorkspace follows project name changes while workspace is untouched", async () => {
  const { syncDefaultProjectWorkspace } = await importProjectFormModule();

  assert.equal(
    syncDefaultProjectWorkspace({
      previousName: "Demo Project",
      nextName: "Demo Project: Alpha?",
      currentWorkspace: "~/.agw/Demo_Project",
    }),
    "~/.agw/Demo_Project_Alpha",
  );
});

test("syncDefaultProjectWorkspace preserves a custom workspace", async () => {
  const { syncDefaultProjectWorkspace } = await importProjectFormModule();

  assert.equal(
    syncDefaultProjectWorkspace({
      previousName: "Demo Project",
      nextName: "Demo Project: Alpha?",
      currentWorkspace: "~/custom",
    }),
    "~/custom",
  );
});

test("resolveCreateProjectWorkspace uses the default when workspace is blank", async () => {
  const { resolveCreateProjectWorkspace } = await importProjectFormModule();

  assert.equal(resolveCreateProjectWorkspace("Demo Project", "  "), "~/.agw/Demo_Project");
});

test("Project Extra Settings accepts blank, arrays, and scalars but rejects malformed JSON", async () => {
  const { getProjectExtraSettingsError, normalizeProjectExtraSettings } =
    await importProjectFormModule();

  assert.equal(getProjectExtraSettingsError("  "), null);
  assert.equal(getProjectExtraSettingsError("[1, 2]"), null);
  assert.equal(getProjectExtraSettingsError('"value"'), null);
  assert.equal(getProjectExtraSettingsError("42"), null);
  assert.equal(getProjectExtraSettingsError("{"), "Settings must be valid JSON.");
  assert.equal(normalizeProjectExtraSettings(" [1, 2] "), "[1, 2]");
  assert.equal(normalizeProjectExtraSettings("  "), null);
});

test("serializeProjectCapabilities always sends explicit empty capability values", async () => {
  const { serializeProjectCapabilities } = await importProjectFormModule();

  assert.deepEqual(
    serializeProjectCapabilities({
      selectedTools: [],
      selectedSkillIds: [],
      selectedMcpToolServerIds: [],
      selectedAppInstanceIds: [],
      environmentVariables: {},
    }),
    {
      tools: "[]",
      skillIds: [],
      mcpToolServerIds: [],
      appInstanceIds: [],
      environmentVariables: {},
    },
  );
});

test("toProjectCapabilityFormState backfills all five capabilities", async () => {
  const { toProjectCapabilityFormState } = await importProjectFormModule();

  assert.deepEqual(
    toProjectCapabilityFormState({
      tools: '["tool-a"]',
      projectSkillRelations: [{ projectId: "project", skillId: "skill" }],
      projectMcpToolServers: [{ projectId: "project", mcpToolServerId: "mcp" }],
      projectAppRelations: [{ projectId: "project", appInstanceId: "app" }],
      environmentVariables: { API_TOKEN: "secret" },
    }),
    {
      selectedTools: ["tool-a"],
      selectedSkillIds: ["skill"],
      selectedMcpToolServerIds: ["mcp"],
      selectedAppInstanceIds: ["app"],
      environmentVariables: { API_TOKEN: "secret" },
    },
  );
});

test("toProjectCapabilityFormState treats malformed tools JSON as an empty selection", async () => {
  const { toProjectCapabilityFormState } = await importProjectFormModule();

  const state = toProjectCapabilityFormState({
    tools: "not-json",
    projectSkillRelations: [],
    projectMcpToolServers: [],
    projectAppRelations: [],
    environmentVariables: {},
  });

  assert.deepEqual(state.selectedTools, []);
});
