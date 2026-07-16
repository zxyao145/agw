import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CREATE_DIALOG_URL = new URL("./create-provider-dialog.tsx", import.meta.url);
const EDIT_DIALOG_URL = new URL("./edit-provider-dialog.tsx", import.meta.url);
const FORM_FIELDS_URL = new URL("./provider-form-fields.tsx", import.meta.url);
const MODELS_EDITOR_URL = new URL("./provider-models-editor.tsx", import.meta.url);
const AUTH_CONFIG_EDITOR_URL = new URL("./provider-auth-config-editor.tsx", import.meta.url);
const TYPES_URL = new URL("./types.ts", import.meta.url);

test("Create and Edit Provider dialogs use the full-screen shell with header actions", async () => {
  for (const fileUrl of [CREATE_DIALOG_URL, EDIT_DIALOG_URL]) {
    const source = await readFile(fileUrl, "utf8");

    assert.match(source, /fixed inset-0 h-screen w-screen max-w-none/);
    assert.match(source, /showCloseButton=\{false\}/);
    assert.match(source, /onInteractOutside=\{\(event\) => event\.preventDefault\(\)\}/);
    assert.match(source, /<DialogHeader className="shrink-0 border-b px-6 py-4">/);
    assert.match(source, /<DialogClose asChild>/);
    assert.match(source, /<ProviderFormFields/);
  }
});

test("Provider form uses a responsive 400px metadata column and two tabs", async () => {
  const source = await readFile(FORM_FIELDS_URL, "utf8");

  assert.match(source, /lg:grid-cols-\[400px_minmax\(0,1fr\)\]/);
  assert.match(source, /<Tabs defaultValue="auth-configs"/);
  assert.match(source, /<TabsTrigger value="auth-configs">Auth Configs<\/TabsTrigger>/);
  assert.match(source, /<TabsTrigger value="models">Models<\/TabsTrigger>/);
  assert.match(source, /<ProviderAuthConfigEditor/);
  assert.match(source, /<ProviderModelsEditor/);

  const nameIndex = source.indexOf(">Name</Label>");
  const endpointIndex = source.indexOf(">Endpoint</Label>");
  const providerTypeIndex = source.indexOf(">Provider type</Label>");
  const descriptionIndex = source.indexOf(">Description</Label>");
  assert.ok(nameIndex < endpointIndex);
  assert.ok(endpointIndex < providerTypeIndex);
  assert.ok(providerTypeIndex < descriptionIndex);
});

test("Provider dialogs submit the complete modelNames draft", async () => {
  for (const fileUrl of [CREATE_DIALOG_URL, EDIT_DIALOG_URL]) {
    const source = await readFile(fileUrl, "utf8");
    assert.match(source, /modelNames: selectedModelNames/);
  }
});

test("Models editor discovers with the current draft and does not select results automatically", async () => {
  const source = await readFile(MODELS_EDITOR_URL, "utf8");

  assert.match(source, /apiPost\("\/api\/providers\/discover-models"/);
  assert.match(source, /providerType,/);
  assert.match(source, /endpoint,/);
  assert.match(source, /apiKey,/);
  assert.match(source, /setDiscoveredModelNames/);
  assert.doesNotMatch(source, /setSelectedModelNames\([^)]*result/);
  assert.match(source, /isProviderModelDiscoverySupported\(providerType\)/);
  assert.match(source, /findDiscoveryApiKey\(authConfigs\)/);
});

test("Models editor explains the provider and ApiKey requirements on the fetch button", async () => {
  const source = await readFile(MODELS_EDITOR_URL, "utf8");

  assert.match(
    source,
    /import \{ Tooltip, TooltipContent, TooltipTrigger \} from "@\/components\/ui\/tooltip"/,
  );
  assert.match(source, /<TooltipTrigger asChild>/);
  assert.match(
    source,
    /<TooltipContent[\s\S]*Only OpenAI APIs are supported[\s\S]*ApiKey[\s\S]*<\/TooltipContent>/,
  );
});

test("Provider auth config exposes only ApiKey authentication", async () => {
  const [editorSource, typesSource] = await Promise.all([
    readFile(AUTH_CONFIG_EDITOR_URL, "utf8"),
    readFile(TYPES_URL, "utf8"),
  ]);

  assert.match(typesSource, /export type ProviderAuthType = "ApiKey";/);
  assert.doesNotMatch(editorSource, /EnvVariable|Environment variable/);
  assert.match(editorSource, /API key \/ bearer token/);
});
