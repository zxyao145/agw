import assert from "node:assert/strict";
import test from "node:test";
import { EMPTY_TOKEN_USAGE } from "@agw/api";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, act, fireEvent, render, screen, waitFor } = await setupDomEnvironment();
const { TooltipProvider } = await import("@agw/components");
const { ChatAside } = await import("./chat-aside.tsx");

test("todo description appears in a tooltip when its title receives focus", async () => {
  render(
    React.createElement(
      TooltipProvider,
      null,
      React.createElement(ChatAside, {
        usage: EMPTY_TOKEN_USAGE,
        todos: [
          {
            id: "todo-1",
            title: "Build the project",
            description: "Run dotnet build",
            isComplete: false,
          },
        ],
      }),
    ),
  );

  assert.equal(screen.queryByText("Run dotnet build"), null);

  await act(async () => {
    fireEvent.focus(screen.getByText("Build the project"));
  });

  await waitFor(() => {
    assert.match(screen.getByRole("tooltip").textContent ?? "", /Run dotnet build/);
  });
});
