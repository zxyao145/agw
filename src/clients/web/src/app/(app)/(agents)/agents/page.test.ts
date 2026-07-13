import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("agents page loads integration app instances and passes app selection state into both dialogs", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /queryKey: \["appInstances"\]/);
  assert.match(source, /apiGet\("\/api\/integrations\/app-instances"\)/);
  assert.match(source, /selectedAppInstanceIds/);
  assert.match(source, /toggleAppInstance/);
  assert.match(source, /selectedAppInstanceIds=\{selectedAppInstanceIds\}/);
  assert.match(source, /selectedAppInstanceIds=\{editSelectedAppInstanceIds\}/);
  assert.match(source, /setEditModelProviderId\(agent\.modelProviderId \?\? ""\)/);
});

test("agents page owns Create and Edit environment-variable state", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /const \[environmentVariables, setEnvironmentVariables\]/);
  assert.match(source, /const \[editEnvironmentVariables, setEditEnvironmentVariables\]/);
  assert.match(
    source,
    /setEditEnvironmentVariables\(toAgentEnvironmentVariableEntries\(agent\.environmentVariables\)\)/,
  );
  assert.match(source, /environmentVariables=\{environmentVariables\}/);
  assert.match(source, /environmentVariables=\{editEnvironmentVariables\}/);
  assert.match(source, /setEnvironmentVariables\(\[\]\)/);
});

test("agents page owns and initializes Create and Edit summary state", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /const \[enableSummary, setEnableSummary\] = React\.useState\(false\)/);
  assert.match(
    source,
    /const \[editEnableSummary, setEditEnableSummary\] = React\.useState\(false\)/,
  );
  assert.match(source, /setEditEnableSummary\(agent\.enableSummary\)/);
  assert.match(
    source,
    /const \[summaryModelProviderId, setSummaryModelProviderId\] = React\.useState\(""\)/,
  );
  assert.match(
    source,
    /const \[editSummaryModelProviderId, setEditSummaryModelProviderId\] = React\.useState\(""\)/,
  );
  assert.match(source, /setEditSummaryModelProviderId\(agent\.summaryModelProviderId \?\? ""\)/);
  assert.match(source, /enableSummary=\{enableSummary\}/);
  assert.match(source, /enableSummary=\{editEnableSummary\}/);
  assert.match(source, /summaryModelProviderId=\{summaryModelProviderId\}/);
  assert.match(source, /summaryModelProviderId=\{editSummaryModelProviderId\}/);
  assert.match(source, /setEnableSummary\(false\)/);
  assert.match(source, /setSummaryModelProviderId\(""\)/);
});
