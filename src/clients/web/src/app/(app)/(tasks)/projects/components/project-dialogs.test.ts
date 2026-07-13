import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CREATE_DIALOG_URL = new URL("./create-project-dialog.tsx", import.meta.url);
const EDIT_DIALOG_URL = new URL("./edit-project-dialog.tsx", import.meta.url);
const FORM_FIELDS_URL = new URL("./project-form-fields.tsx", import.meta.url);
const PAGE_URL = new URL("../page.tsx", import.meta.url);

async function readSource(url: URL, label: string) {
  try {
    return await readFile(url, "utf8");
  } catch (error) {
    assert.fail(`${label} is missing: ${String(error)}`);
  }
}

test("Create and Edit Project are separate full-screen dialogs with upper-right actions", async () => {
  for (const [url, action] of [
    [CREATE_DIALOG_URL, "Create"],
    [EDIT_DIALOG_URL, "Update"],
  ] as const) {
    const source = await readSource(url, `${action} Project dialog`);

    assert.match(source, /fixed inset-0 h-screen w-screen max-w-none/);
    assert.match(source, /showCloseButton=\{false\}/);
    assert.match(source, /onInteractOutside=\{\(event\) => event\.preventDefault\(\)\}/);
    assert.match(source, /onPointerDownOutside=\{\(event\) => event\.preventDefault\(\)\}/);
    assert.match(
      source,
      new RegExp(`DialogHeader[\\s\\S]*Cancel[\\s\\S]*${action}[\\s\\S]*ProjectFormFields`),
    );
    assert.match(source, /ref=\{setDialogPortalContainer\}/);
    assert.match(source, /dialogPortalContainer=\{dialogPortalContainer\}/);
  }
});

test("Project dialogs cannot close or reopen through Dialog while their mutation is pending", async () => {
  for (const [url, mutationName] of [
    [CREATE_DIALOG_URL, "createProjectMutation"],
    [EDIT_DIALOG_URL, "updateProjectMutation"],
  ] as const) {
    const source = await readSource(url, "Project dialog");

    assert.match(source, /onOpenChange=\{\(nextOpen\) =>/);
    assert.match(source, new RegExp(`isPending: ${mutationName}\\.isPending`));
    assert.match(
      source,
      new RegExp(
        `type="button"[\\s\\S]*variant="outline"[\\s\\S]*size="sm"[\\s\\S]*disabled=\\{${mutationName}\\.isPending\\}[\\s\\S]*>\\s*Cancel`,
      ),
    );
  }

  const pageSource = await readSource(PAGE_URL, "Projects page");
  assert.match(pageSource, /setCreateOpen\(false\)/);
  assert.match(pageSource, /setEditOpen\(false\)/);
  assert.match(pageSource, /if \(updateProjectMutation\.isPending\) \{\s*return;\s*\}/);
});

test("Project form has the 400px metadata column and exactly five shared capability tabs", async () => {
  const source = await readSource(FORM_FIELDS_URL, "Project form fields");

  assert.match(source, /lg:grid-cols-\[400px_minmax\(0,1fr\)\]/);
  assert.match(source, /<Tabs defaultValue="skills"/);
  assert.equal(source.match(/<TabsTrigger value=/g)?.length, 5);
  assert.match(source, /<TabsTrigger value="skills">Skills<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="tools">Tools<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="mcp-tool-servers">MCP Tool Server<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="apps">Apps<\/TabsTrigger>/);
  assert.match(
    source,
    /<TabsTrigger value="environment-variables">Environment Variables<\/TabsTrigger>/,
  );
  assert.match(source, /Project Type/);
  assert.match(source, /value="User Defined"/);
  assert.match(source, /readOnly/);
  assert.equal(source.match(/dialogPortalContainer=\{dialogPortalContainer\}/g)?.length, 4);
});

test("Project dialogs serialize all five capabilities into Create and Update payloads", async () => {
  for (const url of [CREATE_DIALOG_URL, EDIT_DIALOG_URL]) {
    const source = await readSource(url, "Project dialog");

    assert.match(source, /serializeProjectCapabilities\(\{/);
    assert.match(source, /selectedTools/);
    assert.match(source, /selectedSkillIds/);
    assert.match(source, /selectedMcpToolServerIds/);
    assert.match(source, /selectedAppInstanceIds/);
    assert.match(source, /environmentVariables/);
  }
});

test("Projects page backfills Edit capabilities and resets every Create capability state", async () => {
  const source = await readSource(PAGE_URL, "Projects page");

  assert.match(source, /toProjectCapabilityFormState\(project\)/);
  assert.match(source, /setSelectedTools\(\[\]\)/);
  assert.match(source, /setSelectedSkillIds\(\[\]\)/);
  assert.match(source, /setSelectedMcpToolServerIds\(\[\]\)/);
  assert.match(source, /setSelectedAppInstanceIds\(\[\]\)/);
  assert.match(source, /setAppSearchTerm\(""\)/);
  assert.match(source, /setEnvironmentVariables\(\[\]\)/);
  assert.match(source, /<CreateProjectDialog/);
  assert.match(source, /<EditProjectDialog/);
});

test("Built-in Projects cannot open or submit the edit dialog", async () => {
  const [pageSource, editSource] = await Promise.all([
    readSource(PAGE_URL, "Projects page"),
    readSource(EDIT_DIALOG_URL, "Edit Project dialog"),
  ]);

  assert.match(pageSource, /if \(project\.type !== 0\) \{\s*return;\s*\}/);
  assert.match(pageSource, /disabled=\{project\.type !== 0\}/);
  assert.doesNotMatch(pageSource, /Only the enable toggle is available/);
  assert.match(editSource, /editingProject\.type !== 0/);
});
