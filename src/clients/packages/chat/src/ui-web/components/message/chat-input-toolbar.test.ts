import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { AgentCommandSuggestion, CommandSource } from "../../../lib/chat/search-command";
import type { AgentMode, PermissionMode } from "@agw/chat-runtime";

const { React, act, fireEvent, render, screen } = await setupDomEnvironment();
const { ChatInputToolbar } = await import("./chat-input-toolbar.tsx");

const modeCommand: AgentCommandSuggestion = { text: "/mode_set", kind: "tool", description: "" };
const skillCommand: AgentCommandSuggestion = {
  text: "/review",
  kind: "skill",
  description: "Review the current diff",
};
const toolCommand: AgentCommandSuggestion = {
  text: "/search",
  kind: "tool",
  description: "Search the workspace",
};

function systemSource(suggestions: AgentCommandSuggestion[]): CommandSource {
  return { mode: "system", suggestions } as CommandSource;
}

type ToolbarOptions = {
  commandSource?: CommandSource;
  agentMode?: AgentMode;
  permissionMode?: PermissionMode;
  activePermissionMode?: PermissionMode | null;
  permissionChangePending?: boolean;
  supportedPermissionModes?: readonly PermissionMode[];
  permissionReason?: string;
  permissionUnavailable?: string;
  isExecuting?: boolean;
  isTransitioning?: boolean;
  commands?: string[];
  permissionModes?: PermissionMode[];
  agentModes?: AgentMode[];
};

function renderToolbar(options: ToolbarOptions = {}) {
  return render(
    React.createElement(ChatInputToolbar, {
      commandSource:
        options.commandSource ?? systemSource([modeCommand, skillCommand, toolCommand]),
      isExecuting: options.isExecuting ?? false,
      isTransitioning: options.isTransitioning ?? false,
      permissionMode: options.permissionMode ?? "alwaysAsk",
      activePermissionMode: options.activePermissionMode,
      permissionChangePending: options.permissionChangePending,
      supportedPermissionModes: options.supportedPermissionModes,
      permissionReason: options.permissionReason,
      permissionUnavailable: options.permissionUnavailable,
      agentMode: options.agentMode ?? "execute",
      onCommandSelect: (command: string) => options.commands?.push(command),
      onPermissionModeChange: (mode: PermissionMode) => options.permissionModes?.push(mode),
      onAgentModeChange: (mode: AgentMode) => options.agentModes?.push(mode),
    }),
  );
}

async function openAddMenu() {
  await act(async () => {
    fireEvent.pointerDown(screen.getByRole("button", { name: "Add" }), {
      button: 0,
      ctrlKey: false,
      pointerType: "mouse",
    });
  });
  return screen.findByRole("menu");
}

async function openPermissionMenu() {
  await act(async () => {
    fireEvent.click(screen.getByRole("combobox", { name: "Tool permission mode" }));
  });
  return screen.findByRole("listbox");
}

test("the Add menu groups plan mode, skills, and tools", async () => {
  renderToolbar();
  await openAddMenu();

  assert.ok(screen.getByText("Plan mode"));
  assert.ok(screen.getByText("Skills"));
  assert.ok(screen.getByText("Tools"));
  assert.ok(screen.getByText("/review"));
  assert.ok(screen.getByText("/search"));
});

test("mode commands stay out of the tool list", async () => {
  renderToolbar({
    commandSource: systemSource([
      modeCommand,
      { text: "/mode_get", kind: "tool", description: "" },
      toolCommand,
    ]),
  });
  await openAddMenu();

  assert.equal(screen.queryByText("/mode_set"), null);
  assert.equal(screen.queryByText("/mode_get"), null);
  assert.ok(screen.getByText("/search"));
});

test("selecting a capability reports its command", async () => {
  const commands: string[] = [];
  renderToolbar({ commands });
  await openAddMenu();

  await act(async () => {
    fireEvent.click(screen.getByText("/review"));
  });

  assert.deepEqual(commands, ["/review"]);
});

test("plan mode toggles from the Add menu", async () => {
  const agentModes: AgentMode[] = [];
  const view = renderToolbar({ agentModes, agentMode: "execute" });
  await openAddMenu();
  assert.ok(screen.getByText("Turn plan mode on"));

  await act(async () => {
    fireEvent.click(screen.getByText("Plan mode"));
  });
  assert.deepEqual(agentModes, ["plan"]);
  view.unmount();

  renderToolbar({ agentModes, agentMode: "plan" });
  await openAddMenu();
  assert.ok(screen.getByText("Turn plan mode off"));
});

test("a target without capabilities says so", async () => {
  renderToolbar({ commandSource: { mode: "agent", suggestions: [] } as unknown as CommandSource });
  await openAddMenu();

  assert.ok(screen.getByText("No actions are available for this target."));
  assert.equal(screen.queryByText("Plan mode"), null);
});

test("the permission control shows the current mode and offers all three", async () => {
  renderToolbar({ permissionMode: "fullAccess" });

  assert.ok(screen.getByRole("combobox", { name: "Tool permission mode" }));
  assert.ok(screen.getByText("Full access"));
  await openPermissionMenu();

  assert.deepEqual(
    screen.getAllByRole("option").map((option) => option.textContent),
    ["Full access", "Always ask", "Allow same arguments"],
  );
});

test("choosing a supported permission mode reports it", async () => {
  const permissionModes: PermissionMode[] = [];
  renderToolbar({ permissionModes });
  await openPermissionMenu();
  const option = screen.getByRole("option", { name: "Full access" });

  await act(async () => {
    fireEvent.click(option);
  });

  assert.deepEqual(permissionModes, ["fullAccess"]);
});

test("an unsupported permission mode cannot be chosen", async () => {
  const permissionModes: PermissionMode[] = [];
  renderToolbar({ permissionModes, supportedPermissionModes: ["fullAccess"] });
  await openPermissionMenu();

  const option = screen.getByRole("option", { name: "Always ask" });
  assert.equal(option.getAttribute("aria-disabled"), "true");
  await act(async () => {
    fireEvent.click(option);
  });

  assert.deepEqual(permissionModes, []);
});

test("a pending permission change announces the current and next turn modes", () => {
  renderToolbar({
    permissionMode: "fullAccess",
    activePermissionMode: "alwaysAsk",
    permissionChangePending: true,
  });

  assert.match(
    screen.getByRole("status").textContent ?? "",
    /Current: Always ask · Next turn: Full access/,
  );
});

test("an unavailable permission control explains why", () => {
  renderToolbar({ permissionUnavailable: "Codex supports full access only" });

  assert.ok(screen.getByText("Codex supports full access only"));
});

test("a transition blocks the permission control", () => {
  renderToolbar({ isTransitioning: true });

  assert.equal(
    screen.getByRole("combobox", { name: "Tool permission mode" }).hasAttribute("disabled"),
    true,
  );
});

test("a running execution keeps the permission control usable", () => {
  renderToolbar({ isExecuting: true });

  assert.equal(
    screen.getByRole("combobox", { name: "Tool permission mode" }).hasAttribute("disabled"),
    false,
  );
});

test("plan mode shows a status chip that turns it off", () => {
  const agentModes: AgentMode[] = [];
  renderToolbar({ agentModes, agentMode: "plan" });

  assert.ok(screen.getByText("Plan"));
  fireEvent.click(screen.getByRole("button", { name: "Turn plan mode off" }));

  assert.deepEqual(agentModes, ["execute"]);
});

test("execute mode shows no plan status chip", () => {
  renderToolbar({ agentMode: "execute" });

  assert.equal(screen.queryByRole("button", { name: "Turn plan mode off" }), null);
});
