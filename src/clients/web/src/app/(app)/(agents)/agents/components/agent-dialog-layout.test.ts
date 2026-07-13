import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CREATE_DIALOG_URL = new URL("./create-agent-dialog.tsx", import.meta.url);
const EDIT_DIALOG_URL = new URL("./edit-agent-dialog.tsx", import.meta.url);
const FORM_FIELDS_URL = new URL("./agent-form-fields.tsx", import.meta.url);
const DROPDOWN_MENU_URL = new URL(
  "../../../../../components/ui/dropdown-menu.tsx",
  import.meta.url,
);
const POPOVER_URL = new URL("../../../../../components/ui/popover.tsx", import.meta.url);
const SELECT_URL = new URL("../../../../../components/ui/select.tsx", import.meta.url);

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

test("Agent form uses a responsive 400px metadata column and six configuration tabs", async () => {
  const source = await readFile(FORM_FIELDS_URL, "utf8");

  assert.match(source, /lg:grid-cols-\[400px_minmax\(0,1fr\)\]/);
  assert.match(source, /<TabsTrigger value="system-prompt">System Prompt<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="skills">Skills<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="tools">Tools<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="mcp-tool-servers">MCP Tool Server<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="apps">Apps<\/TabsTrigger>/);
  assert.match(
    source,
    /<TabsTrigger value="environment-variables">Environment Variables<\/TabsTrigger>/,
  );
  assert.match(source, /<AgentEnvironmentVariablesEditor/);
  assert.match(source, /External agents do not support system prompt configuration/);
  assert.match(source, /External agents do not support skill configuration/);
  assert.match(source, /External agents do not support tool configuration/);
  assert.match(source, /SelectedItemsList/);
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

test("Agent Model Provider Select portals inside the current dialog so wheel scrolling is preserved", async () => {
  const [createSource, editSource, formSource, selectSource] = await Promise.all([
    readFile(CREATE_DIALOG_URL, "utf8"),
    readFile(EDIT_DIALOG_URL, "utf8"),
    readFile(FORM_FIELDS_URL, "utf8"),
    readFile(SELECT_URL, "utf8"),
  ]);

  for (const source of [createSource, editSource]) {
    assert.match(source, /ref=\{setDialogPortalContainer\}/);
    assert.match(source, /dialogPortalContainer=\{dialogPortalContainer\}/);
  }

  assert.match(formSource, /dialogPortalContainer: HTMLElement \| null/);
  assert.match(formSource, /portalContainer=\{dialogPortalContainer\}/);
  assert.match(selectSource, /portalContainer\?: HTMLElement \| null/);
  assert.match(selectSource, /<SelectPrimitive\.Portal container=\{portalContainer\}>/);
});

test("Agent capability selectors portal inside the current dialog so wheel scrolling is preserved", async () => {
  const [formSource, dropdownMenuSource, popoverSource] = await Promise.all([
    readFile(FORM_FIELDS_URL, "utf8"),
    readFile(DROPDOWN_MENU_URL, "utf8"),
    readFile(POPOVER_URL, "utf8"),
  ]);

  assert.equal(formSource.match(/portalContainer=\{dialogPortalContainer\}/g)?.length, 5);
  assert.match(dropdownMenuSource, /portalContainer\?: HTMLElement \| null/);
  assert.match(dropdownMenuSource, /<DropdownMenuPrimitive\.Portal container=\{portalContainer\}>/);
  assert.match(popoverSource, /portalContainer\?: HTMLElement \| null/);
  assert.match(popoverSource, /<PopoverPrimitive\.Portal container=\{portalContainer\}>/);
});
