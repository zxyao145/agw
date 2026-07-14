import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CREATE_DIALOG_URL = new URL("./create-agent-dialog.tsx", import.meta.url);
const EDIT_DIALOG_URL = new URL("./edit-agent-dialog.tsx", import.meta.url);
const FORM_FIELDS_URL = new URL("./agent-form-fields.tsx", import.meta.url);
const PAGE_URL = new URL("../page.tsx", import.meta.url);

test("Create and Edit Agent dialogs use the full-screen Agentflow shell with header actions", async () => {
  for (const fileUrl of [CREATE_DIALOG_URL, EDIT_DIALOG_URL]) {
    const source = await readFile(fileUrl, "utf8");

    assert.match(source, /fixed inset-0 h-screen w-screen max-w-none/);
    assert.match(source, /showCloseButton=\{false\}/);
    assert.match(source, /onInteractOutside=\{\(event\) => event\.preventDefault\(\)\}/);
    assert.match(source, /onPointerDownOutside=\{\(event\) => event\.preventDefault\(\)\}/);
    assert.match(source, /DialogHeader[\s\S]*DialogClose[\s\S]*AgentFormFields/);
  }
});

test("Agent dialogs cannot close or reopen through Dialog while their mutation is pending", async () => {
  for (const [fileUrl, mutationName] of [
    [CREATE_DIALOG_URL, "createAgentMutation"],
    [EDIT_DIALOG_URL, "updateAgentMutation"],
  ] as const) {
    const source = await readFile(fileUrl, "utf8");

    assert.match(source, /onOpenChange=\{\(nextOpen\) =>/);
    assert.match(source, new RegExp(`isPending: ${mutationName}\\.isPending`));
    assert.match(
      source,
      new RegExp(
        `type="button"[\\s\\S]*variant="outline"[\\s\\S]*size="sm"[\\s\\S]*disabled=\\{${mutationName}\\.isPending\\}[\\s\\S]*>\\s*Cancel`,
      ),
    );
  }

  const pageSource = await readFile(PAGE_URL, "utf8");
  assert.match(pageSource, /setCreateOpen\(false\)/);
  assert.match(pageSource, /setEditOpen\(false\)/);
  assert.match(pageSource, /if \(updateAgentMutation\.isPending\) \{\s*return;\s*\}/);
});

test("Agent form uses a responsive 400px metadata column and six configuration tabs", async () => {
  const source = await readFile(FORM_FIELDS_URL, "utf8");

  assert.match(source, /lg:grid-cols-\[400px_minmax\(0,1fr\)\]/);
  assert.match(source, /<TabsTrigger value="system-prompt">Instructions<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="skills">Skills<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="tools">Tools<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="mcp-tool-servers">MCP Tool Server<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="apps">Apps<\/TabsTrigger>/);
  assert.match(
    source,
    /<TabsTrigger value="environment-variables">Environment Variables<\/TabsTrigger>/,
  );
  assert.match(source, /<EnvironmentVariablesPanel/);
  assert.match(source, /External agents do not support instructions configuration/);
  assert.match(source, /External agents do not support skill configuration/);
  assert.match(source, /External agents do not support tool configuration/);
  assert.match(source, /<SkillsPanel/);
});

test("Agent form explains project-level capability merging below the tabs", async () => {
  const source = await readFile(FORM_FIELDS_URL, "utf8");
  const normalizedSource = source.replace(/\s+/g, " ");
  const tabsListEnd = normalizedSource.indexOf("</TabsList>");
  const description = normalizedSource.indexOf(
    "Agw recommends configuring Skills, Tools, MCP Tool Servers, Apps, and Environment Variables in the Project.",
  );

  assert.ok(description > tabsListEnd);
  assert.match(
    normalizedSource,
    /When the agent runs, it merges the configurations from both the agent and the project\./,
  );
});

test("Edit Agent only sends editable Extra Settings for External Agent updates", async () => {
  const source = await readFile(EDIT_DIALOG_URL, "utf8");

  assert.match(source, /extra: isExternalAgent \? normalizeAgentExtraSettings\(extra\) : null/);
});

test("Create and Edit Agent dialogs send normalized environment variables", async () => {
  for (const fileUrl of [CREATE_DIALOG_URL, EDIT_DIALOG_URL]) {
    const source = await readFile(fileUrl, "utf8");

    assert.match(
      source,
      /environmentVariables: normalizeAgentEnvironmentVariables\(environmentVariables\)/,
    );
    assert.match(source, /getAgentEnvironmentVariablesError\(environmentVariables\)/);
  }
});

test("All Agent forms expose and submit the optional turn summary setting", async () => {
  const [createSource, editSource, formSource] = await Promise.all([
    readFile(CREATE_DIALOG_URL, "utf8"),
    readFile(EDIT_DIALOG_URL, "utf8"),
    readFile(FORM_FIELDS_URL, "utf8"),
  ]);

  assert.match(formSource, /enableSummary: boolean/);
  assert.match(formSource, /setEnableSummary: \(value: boolean\) => void/);
  assert.match(formSource, /summaryModelProviderId: string/);
  assert.match(formSource, /setSummaryModelProviderId: \(value: string\) => void/);
  assert.match(formSource, /Summary Model Provider/);
  assert.match(
    formSource,
    /isExternalAgent\s*\?\s*summaryModelProviderId\s*:\s*summaryModelProviderId \|\| modelProviderId/,
  );
  assert.doesNotMatch(formSource, /\{!isExternalAgent \? \([\s\S]*Generate Turn Summary/);
  assert.match(formSource, /checked=\{enableSummary\}/);
  assert.match(formSource, /onCheckedChange=\{setEnableSummary\}/);
  assert.match(createSource, /enableSummary,/);
  assert.match(createSource, /summaryModelProviderId: summaryModelProviderId \|\| null/);
  assert.doesNotMatch(editSource, /enableSummary: !isExternalAgent && enableSummary/);
  assert.match(editSource, /summaryModelProviderId: summaryModelProviderId \|\| null/);
  assert.match(editSource, /enableSummary && !effectiveSummaryModelProviderId/);
});

test("Agent forms use SearchableSelect for both model provider fields", async () => {
  const source = await readFile(FORM_FIELDS_URL, "utf8");

  assert.match(source, /SearchableSelect,[\s\S]*type SearchableSelectOption/);
  assert.match(source, /const modelProviderOptions = React\.useMemo<SearchableSelectOption\[]>/);
  assert.equal(source.match(/<SearchableSelect\s/g)?.length, 2);
  assert.equal(source.match(/options=\{modelProviderOptions\}/g)?.length, 2);
  assert.doesNotMatch(source, /<Select(?:\s|>)/);
});

test("Agent dialogs use inline searchable selectors without portal wiring", async () => {
  const [createSource, editSource, formSource] = await Promise.all([
    readFile(CREATE_DIALOG_URL, "utf8"),
    readFile(EDIT_DIALOG_URL, "utf8"),
    readFile(FORM_FIELDS_URL, "utf8"),
  ]);

  for (const source of [createSource, editSource]) {
    assert.doesNotMatch(source, /setDialogPortalContainer/);
    assert.doesNotMatch(source, /dialogPortalContainer=\{dialogPortalContainer\}/);
  }

  assert.doesNotMatch(formSource, /dialogPortalContainer/);
});
