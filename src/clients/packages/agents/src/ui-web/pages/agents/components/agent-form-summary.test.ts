import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";
import * as React from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { UseQueryResult } from "@agw/components/query";
import { AgentType, ExternalAgentKind } from "./types";

const testRequire = createRequire(import.meta.url);
const componentsRequire = createRequire(testRequire.resolve("@agw/components"));
// Keep the workspace component package and React DOM on the same hook dispatcher.
for (const specifier of ["react", "react/jsx-runtime", "react/jsx-dev-runtime", "react-dom"]) {
  const sharedPath = testRequire.resolve(specifier);
  testRequire(sharedPath);
  testRequire.cache[componentsRequire.resolve(specifier)] = testRequire.cache[sharedPath];
}
const { AgentFormFields } = await import("./agent-form-fields");

type FormProps = React.ComponentProps<typeof AgentFormFields>;
const ignoreChange = () => undefined;

function loadedQuery<T>(data: T): UseQueryResult<T, Error> {
  return { data, isLoading: false } as UseQueryResult<T, Error>;
}

function renderForm(overrides: Partial<FormProps>): string {
  return renderToStaticMarkup(
    React.createElement(AgentFormFields, {
      mode: "edit",
      displayName: "Agent",
      setDisplayName: ignoreChange,
      name: "agent",
      setName: ignoreChange,
      description: "",
      setDescription: ignoreChange,
      systemPrompt: "",
      setSystemPrompt: ignoreChange,
      modelProviderId: "",
      setModelProviderId: ignoreChange,
      summaryModelProviderId: "legacy-summary-provider",
      setSummaryModelProviderId: ignoreChange,
      enableSummary: true,
      setEnableSummary: ignoreChange,
      agentType: AgentType.System,
      externalAgentKind: ExternalAgentKind.None,
      externalAgentOptionsQuery: loadedQuery([]),
      extra: "",
      setExtra: ignoreChange,
      responseSchema: "",
      setResponseSchema: ignoreChange,
      environmentVariables: [],
      setEnvironmentVariables: ignoreChange,
      selectedSkillIds: [],
      connectionOptions: [],
      selectedConnectionIds: [],
      toggleConnection: ignoreChange,
      tools: [],
      setTools: ignoreChange,
      agentOptions: [],
      modelProvidersQuery: loadedQuery([]),
      skillsQuery: loadedQuery([]),
      toolsQuery: loadedQuery([]),
      mcpToolServersQuery: loadedQuery([]),
      toggleSkill: ignoreChange,
      selectedMcpToolServerIds: [],
      toggleMcpToolServer: ignoreChange,
      idPrefix: "test-",
      ...overrides,
    }),
  );
}

for (const kind of [ExternalAgentKind.ClaudeCode, ExternalAgentKind.Codex, ExternalAgentKind.Pi]) {
  test(`External Agent ${kind} hides all summary controls despite stale enabled state`, () => {
    const markup = renderForm({ agentType: AgentType.External, externalAgentKind: kind });

    assert.doesNotMatch(markup, /Generate Turn Summary/);
    assert.doesNotMatch(markup, /Summary Model Provider/);
    assert.doesNotMatch(markup, /id="test-(?:enableSummary|summaryModelProviderId)"/);
  });
}

test("System Agent shows the summary toggle and selected provider", () => {
  const markup = renderForm({});

  assert.match(markup, /Generate Turn Summary/);
  assert.match(markup, /Summary Model Provider/);
  assert.match(markup, /id="test-enableSummary"/);
});

test("System Agent with summary disabled hides only the provider selector", () => {
  const markup = renderForm({ enableSummary: false });

  assert.match(markup, /id="test-enableSummary"/);
  assert.doesNotMatch(markup, /id="test-summaryModelProviderId"/);
});
