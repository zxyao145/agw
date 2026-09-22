import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { UseQueryResult } from "@agw/components/query";
import type { PagedResult } from "@agw/components";
import type { AgentDto } from "./types";

const { React, act, fireEvent, render, screen, waitFor } = await setupDomEnvironment();
const { AgentsTable } = await import("./agents-table.tsx");
const { TooltipProvider } = await import("@agw/components");

function agent(overrides: Partial<AgentDto> = {}): AgentDto {
  return {
    id: "11111111-1111-1111-1111-000000000001",
    name: "reviewer",
    displayName: "Reviewer",
    description: "Reviews the current diff",
    systemPrompt: "You review code.",
    type: 0,
    enable: true,
    tools: [],
    createTime: "2026-09-20T01:00:00Z",
    updateTime: "2026-09-21T01:00:00Z",
    ...overrides,
  } as unknown as AgentDto;
}

type Query = UseQueryResult<PagedResult<AgentDto>, Error>;

function queryOf(state: {
  items?: AgentDto[];
  isLoading?: boolean;
  isError?: boolean;
  error?: Error;
}): Query {
  const items = state.items ?? [];
  return {
    data: { items, total: items.length, pageIndex: 1, pageSize: 20 },
    isLoading: state.isLoading ?? false,
    isError: state.isError ?? false,
    error: state.error ?? null,
  } as unknown as Query;
}

type TableOptions = {
  agentsQuery?: Query;
  pendingEnabledAgentIds?: ReadonlySet<string>;
  isCopying?: boolean;
  edits?: AgentDto[];
  copies?: AgentDto[];
  deletions?: AgentDto[];
  executions?: AgentDto[];
  enabledChanges?: { agent: AgentDto; enable: boolean }[];
};

function renderTable(options: TableOptions = {}) {
  return render(
    React.createElement(
      TooltipProvider,
      null,
      React.createElement(AgentsTable, {
        agentsQuery: options.agentsQuery ?? queryOf({ items: [agent()] }),
        onEdit: (value: AgentDto) => options.edits?.push(value),
        onCopy: (value: AgentDto) => options.copies?.push(value),
        onDelete: (value: AgentDto) => options.deletions?.push(value),
        onExecute: (value: AgentDto) => options.executions?.push(value),
        onEnabledChange: (value: AgentDto, enable: boolean) =>
          options.enabledChanges?.push({ agent: value, enable }),
        pendingEnabledAgentIds: options.pendingEnabledAgentIds ?? new Set<string>(),
        isCopying: options.isCopying ?? false,
      }),
    ),
  );
}

test("a loading catalog shows progress instead of a table", () => {
  renderTable({ agentsQuery: queryOf({ isLoading: true }) });

  assert.ok(screen.getByText("Loading..."));
  assert.equal(screen.queryByRole("table"), null);
});

test("a failing catalog reports the server error", () => {
  renderTable({
    agentsQuery: queryOf({ isError: true, error: new Error("Agents unavailable") }),
  });

  assert.ok(screen.getByText(/Failed to load agents: Agents unavailable/));
});

test("an empty catalog invites creating the first agent", () => {
  renderTable({ agentsQuery: queryOf({ items: [] }) });

  assert.ok(screen.getByText("No agents found. Create one to get started."));
  assert.equal(screen.queryByRole("table"), null);
});

test("an agent row shows its identity, type, and effective update time", () => {
  renderTable({ agentsQuery: queryOf({ items: [agent()] }) });

  assert.ok(screen.getByText("reviewer"));
  assert.ok(screen.getByText("11111111-1111-1111-1111-000000000001"));
  assert.ok(screen.getByText("Reviewer"));
  assert.ok(screen.getByText("System"));
  assert.ok(screen.getByRole("columnheader", { name: "Updated" }));
});

test("an external agent row names its kind", () => {
  const view = renderTable({
    agentsQuery: queryOf({
      items: [agent({ type: 1, externalAgentKind: 1 } as Partial<AgentDto>)],
    }),
  });
  assert.match(screen.getByText(/External ·/).textContent ?? "", /External · Claude Code/);
  view.unmount();

  renderTable({
    agentsQuery: queryOf({
      items: [agent({ type: 1, externalAgentKind: 9 } as Partial<AgentDto>)],
    }),
  });
  assert.match(screen.getByText(/External ·/).textContent ?? "", /External · Unknown/);
});

test("empty description and instructions fall back to a dash", () => {
  const view = renderTable({
    agentsQuery: queryOf({ items: [agent({ description: "", systemPrompt: "" })] }),
  });

  assert.equal(view.container.querySelectorAll("td span.text-muted-foreground").length >= 2, true);
  assert.equal(screen.queryByText("You review code."), null);
});

test("instructions are truncated in the cell and shown in full on hover", async () => {
  renderTable({ agentsQuery: queryOf({ items: [agent({ systemPrompt: "Review every diff." })] }) });
  const trigger = screen.getByText("Review every diff.");

  await act(async () => {
    fireEvent.focus(trigger);
  });

  await waitFor(() => {
    assert.match(screen.getByRole("tooltip").textContent ?? "", /Review every diff\./);
  });
});

test("tool names beyond the first two are summarized by count", () => {
  const view = renderTable({
    agentsQuery: queryOf({
      items: [
        agent({
          tools: ["read_file", "write_file", "command_execution", "search"].map((name) => ({
            definition: { name },
          })),
        } as Partial<AgentDto>),
      ],
    }),
  });

  assert.match(view.container.textContent ?? "", /read_file, write_file \+2 more/);
});

test("the enabled switch reports its new state", () => {
  const enabledChanges: { agent: AgentDto; enable: boolean }[] = [];
  renderTable({ enabledChanges });

  const toggle = screen.getByRole("switch", { name: "Reviewer enabled" });
  assert.equal(toggle.getAttribute("aria-checked"), "true");
  fireEvent.click(toggle);

  assert.deepEqual(
    enabledChanges.map((change) => change.enable),
    [false],
  );
});

test("an agent with a pending change cannot be toggled again", () => {
  const enabledChanges: { agent: AgentDto; enable: boolean }[] = [];
  renderTable({
    enabledChanges,
    pendingEnabledAgentIds: new Set(["11111111-1111-1111-1111-000000000001"]),
  });

  const toggle = screen.getByRole("switch", { name: "Reviewer enabled" });
  assert.equal(toggle.hasAttribute("disabled"), true);
  fireEvent.click(toggle);

  assert.deepEqual(enabledChanges, []);
});

test("row actions report the agent they act on", () => {
  const edits: AgentDto[] = [];
  const copies: AgentDto[] = [];
  const deletions: AgentDto[] = [];
  const executions: AgentDto[] = [];
  const view = renderTable({ edits, copies, deletions, executions });

  for (const action of view.container.querySelectorAll("tbody button")) fireEvent.click(action);

  assert.deepEqual(
    [executions.length, edits.length, copies.length, deletions.length],
    [1, 1, 1, 1],
  );
});

test("a copy in flight blocks the copy action", () => {
  const copies: AgentDto[] = [];
  renderTable({ copies, isCopying: true });

  const copyAction = screen.getByRole("button", { name: "Copy agent" });
  assert.equal(copyAction.hasAttribute("disabled"), true);
  fireEvent.click(copyAction);

  assert.deepEqual(copies, []);
});
