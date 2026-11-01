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

test("resolveCreateProjectWorkspace returns null when workspace is blank", async () => {
  const { resolveCreateProjectWorkspace } = await importProjectFormModule();

  assert.equal(resolveCreateProjectWorkspace("  "), null);
});

test("resolveCreateProjectWorkspace preserves a custom path and trims surrounding whitespace", async () => {
  const { resolveCreateProjectWorkspace } = await importProjectFormModule();

  assert.equal(resolveCreateProjectWorkspace("  ~/custom  "), "~/custom");
});

test("serializeProjectCapabilities always sends explicit empty capability values", async () => {
  const { serializeProjectCapabilities } = await importProjectFormModule();

  assert.deepEqual(
    serializeProjectCapabilities({
      tools: [],
      selectedSkillIds: [],
      selectedMcpToolServerIds: [],
      selectedConnectionIds: [],
      environmentVariables: {},
    }),
    {
      tools: [],
      skillIds: [],
      mcpToolServerIds: [],
      connectionIds: [],
      environmentVariables: {},
    },
  );
});

test("toProjectCapabilityFormState backfills all capabilities", async () => {
  const { toProjectCapabilityFormState } = await importProjectFormModule();
  const tools = [
    {
      kind: "toolBlock" as const,
      definition: {
        name: "project-memory" as const,
        options: { storage: "filesystem" as const },
      },
    },
  ];

  assert.deepEqual(
    toProjectCapabilityFormState({
      tools,
      projectSkillRelations: [{ projectId: "project", skillId: "skill" }],
      projectMcpToolServers: [{ projectId: "project", mcpToolServerId: "mcp" }],
      projectConnectionRelations: [{ projectId: "project", connectionId: "app" }],
      environmentVariables: { API_TOKEN: "secret" },
    }),
    {
      tools,
      selectedSkillIds: ["skill"],
      selectedMcpToolServerIds: ["mcp"],
      selectedConnectionIds: ["app"],
      environmentVariables: { API_TOKEN: "secret" },
    },
  );
});

test("toProjectCapabilityFormState treats missing tools as an empty selection", async () => {
  const { toProjectCapabilityFormState } = await importProjectFormModule();

  const state = toProjectCapabilityFormState({
    projectSkillRelations: [],
    projectMcpToolServers: [],
    projectConnectionRelations: [],
    environmentVariables: {},
  });

  assert.deepEqual(state.tools, []);
});
