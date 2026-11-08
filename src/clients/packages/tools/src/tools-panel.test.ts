import assert from "node:assert/strict";
import test from "node:test";

import { setupDomEnvironment } from "@agw/test-harness";
import type { ToolLiteInfo, ToolValueObject } from "./tool-values";

const environment = await setupDomEnvironment();
const { ToolsPanel } = await import("./tools-panel");

const missingExtensionTool: ToolValueObject = {
  kind: "tool",
  definition: {
    name: "extension",
    extensionId: "removed-extension",
    toolId: "calendar_lookup",
  },
};

const webSearch: ToolLiteInfo = {
  kind: "tool",
  name: "web_search",
  displayName: "Web Search",
  description: "Search the web.",
  category: "General",
  memberToolNames: [],
  scopes: 1,
  requiresWorkspace: false,
  requiresConfirmation: false,
};

test("selecting a Tool keeps an existing Extension Tool that is absent from the catalog", async () => {
  function Harness() {
    const [values, setValues] = environment.React.useState([missingExtensionTool]);
    return environment.React.createElement(ToolsPanel, {
      scope: "agent",
      toolsQuery: { data: [webSearch], isLoading: false } as never,
      values,
      setValues,
    });
  }

  environment.render(environment.React.createElement(Harness));
  await environment.act(async () => {
    environment.fireEvent.click(environment.screen.getByRole("combobox", { name: "Tools" }));
  });
  const option = await environment.screen.findByRole("option", { name: /Web Search/u });
  await environment.act(async () => {
    environment.fireEvent.click(option);
  });

  await environment.waitFor(() => {
    assert.equal(environment.screen.getByText("2 tools selected").textContent, "2 tools selected");
  });
  assert.equal(environment.screen.getByText("calendar_lookup").textContent, "calendar_lookup");
  assert.equal(environment.screen.getAllByText("Web Search").length, 2);
});
