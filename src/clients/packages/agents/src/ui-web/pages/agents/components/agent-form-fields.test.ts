import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { UseQueryResult } from "@agw/components/query";
import { AgentType, ExternalAgentKind } from "./types";

const { React, act, fireEvent, render, screen } = await setupDomEnvironment();
const { AgentFormFields } = await import("./agent-form-fields.tsx");
const { TooltipProvider } = await import("@agw/components");

function emptyQuery<T>(items: T[] = []): UseQueryResult<T[], Error> {
  return {
    data: items,
    isLoading: false,
    isError: false,
    error: null,
  } as unknown as UseQueryResult<T[], Error>;
}

type FormState = {
  displayName: string;
  name: string;
  description: string;
  systemPrompt: string;
  modelProviderId: string;
  summaryModelProviderId: string;
  enableSummary: boolean;
  extra: string;
  responseSchema: string;
};

const initialState: FormState = {
  displayName: "",
  name: "",
  description: "",
  systemPrompt: "",
  modelProviderId: "",
  summaryModelProviderId: "",
  enableSummary: false,
  extra: "",
  responseSchema: "",
};

function Harness({
  agentType = AgentType.System,
  externalAgentKind = ExternalAgentKind.ClaudeCode,
  initial = initialState,
  onState,
}: {
  agentType?: AgentType;
  externalAgentKind?: ExternalAgentKind;
  initial?: FormState;
  onState?: (state: FormState) => void;
}) {
  const [state, setState] = React.useState(initial);
  const update = (patch: Partial<FormState>) =>
    setState((current) => {
      const next = { ...current, ...patch };
      onState?.(next);
      return next;
    });

  return React.createElement(
    TooltipProvider,
    null,
    React.createElement(AgentFormFields, {
      mode: "create",
      displayName: state.displayName,
      setDisplayName: (value: string) => update({ displayName: value }),
      name: state.name,
      setName: (value: string) => update({ name: value }),
      description: state.description,
      setDescription: (value: string) => update({ description: value }),
      systemPrompt: state.systemPrompt,
      setSystemPrompt: (value: string) => update({ systemPrompt: value }),
      modelProviderId: state.modelProviderId,
      setModelProviderId: (value: string) => update({ modelProviderId: value }),
      summaryModelProviderId: state.summaryModelProviderId,
      setSummaryModelProviderId: (value: string) => update({ summaryModelProviderId: value }),
      enableSummary: state.enableSummary,
      setEnableSummary: (value: boolean) => update({ enableSummary: value }),
      agentType,
      setAgentType: () => {},
      externalAgentKind,
      setExternalAgentKind: () => {},
      externalAgentOptionsQuery: emptyQuery([]),
      extra: state.extra,
      setExtra: (value: string) => update({ extra: value }),
      responseSchema: state.responseSchema,
      setResponseSchema: (value: string) => update({ responseSchema: value }),
      environmentVariables: [],
      setEnvironmentVariables: () => {},
      selectedSkillIds: [],
      connectionOptions: [],
      selectedConnectionIds: [],
      toggleConnection: () => {},
      tools: [],
      setTools: () => {},
      agentOptions: [],
      modelProvidersQuery: emptyQuery([]),
      skillsQuery: emptyQuery([]),
      toolsQuery: emptyQuery([]),
      mcpToolServersQuery: emptyQuery([]),
      toggleSkill: () => {},
      selectedMcpToolServerIds: [],
      toggleMcpToolServer: () => {},
    }),
  );
}

function renderForm(props: Parameters<typeof Harness>[0] = {}) {
  return render(React.createElement(Harness, props));
}

function openTab(name: string) {
  fireEvent.mouseDown(screen.getByRole("tab", { name }), { button: 0, ctrlKey: false });
}

test("the form offers the eight agent configuration tabs", () => {
  renderForm();

  assert.deepEqual(
    screen.getAllByRole("tab").map((tab) => tab.textContent),
    [
      "Instructions",
      "Tools",
      "Skills",
      "MCP Tool Server",
      "Integrations",
      "Environment Variables",
      "Response Schema",
      "Extra Settings",
    ],
  );
});

test("identity fields report what the operator types", () => {
  const states: FormState[] = [];
  renderForm({ onState: (state) => states.push(state) });

  fireEvent.change(screen.getByLabelText(/Display Name/), { target: { value: "Reviewer" } });
  fireEvent.change(screen.getByLabelText("Name (Optional)"), { target: { value: "reviewer" } });
  fireEvent.change(screen.getByLabelText(/Description/), { target: { value: "Reviews diffs" } });

  assert.deepEqual(
    [states[0].displayName, states[1].name, states[2].description],
    ["Reviewer", "reviewer", "Reviews diffs"],
  );
});

test("a system agent configures instructions, skills, tools, MCP servers, and integrations", () => {
  renderForm();

  for (const tab of ["Instructions", "Skills", "Tools", "MCP Tool Server", "Integrations"]) {
    openTab(tab);
    assert.equal(screen.queryByText(/External agents do not support/), null, tab);
  }
});

test("an external agent explains which configuration it does not support", () => {
  renderForm({ agentType: AgentType.External });

  const notices: string[] = [];
  for (const tab of ["Instructions", "Skills", "Tools", "MCP Tool Server", "Integrations"]) {
    openTab(tab);
    notices.push(screen.getByText(/External agents do not support/).textContent ?? "");
  }

  assert.deepEqual(notices, [
    "External agents do not support instructions configuration.",
    "External agents do not support skill configuration.",
    "External agents do not support tool configuration.",
    "External agents do not support MCP tool server configuration.",
    "External agents do not support integration configuration.",
  ]);
});

test("an external agent keeps its instructions read-only", () => {
  renderForm({
    agentType: AgentType.External,
    initial: { ...initialState, systemPrompt: "fixed" },
  });
  openTab("Instructions");

  assert.equal(
    (screen.getByDisplayValue("fixed") as HTMLTextAreaElement).hasAttribute("readonly"),
    true,
  );
});

test("Extra Settings is editable only for an external agent", () => {
  const view = renderForm();
  assert.equal(screen.getByRole("tab", { name: "Extra Settings" }).hasAttribute("disabled"), true);
  view.unmount();

  renderForm({ agentType: AgentType.External });
  assert.equal(screen.getByRole("tab", { name: "Extra Settings" }).hasAttribute("disabled"), false);
});

test("a Pi agent cannot configure a response schema", () => {
  const view = renderForm({
    agentType: AgentType.External,
    externalAgentKind: ExternalAgentKind.Pi,
  });

  assert.equal(screen.getByRole("tab", { name: "Response Schema" }).hasAttribute("disabled"), true);
  openTab("Response Schema");
  assert.equal(screen.queryByLabelText("JSON Schema object"), null);
  view.unmount();

  renderForm({ agentType: AgentType.External, externalAgentKind: ExternalAgentKind.ClaudeCode });
  assert.equal(
    screen.getByRole("tab", { name: "Response Schema" }).hasAttribute("disabled"),
    false,
  );
});

test("an invalid response schema is reported next to the editor", async () => {
  renderForm();
  openTab("Response Schema");

  await act(async () => {
    fireEvent.change(screen.getByLabelText("JSON Schema object"), {
      target: { value: "{ not json" },
    });
  });

  assert.ok(screen.getByRole("alert"));
  assert.equal(screen.getByLabelText("JSON Schema object").getAttribute("aria-invalid"), "true");
});

test("a valid response schema clears the report", async () => {
  renderForm({ initial: { ...initialState, responseSchema: "{ not json" } });
  openTab("Response Schema");
  assert.ok(screen.getByRole("alert"));

  await act(async () => {
    fireEvent.change(screen.getByLabelText("JSON Schema object"), {
      target: { value: '{"type":"object"}' },
    });
  });

  assert.equal(screen.queryByRole("alert"), null);
});

test("turn summary settings belong to system agents and reveal their model provider", async () => {
  const external = renderForm({ agentType: AgentType.External });
  assert.equal(screen.queryByLabelText("Generate Turn Summary"), null);
  external.unmount();

  renderForm();
  const toggle = screen.getByRole("switch", { name: "Generate Turn Summary" });
  assert.equal(screen.queryByRole("combobox", { name: "Summary Model Provider" }), null);

  await act(async () => {
    fireEvent.click(toggle);
  });

  assert.ok(screen.getByRole("combobox", { name: "Summary Model Provider" }));
});
