import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { IntegrationSelection, PluginDefinition } from "../types";

const { React, fireEvent, render, screen } = await setupDomEnvironment();
const { PluginCard } = await import("./plugin-card.tsx");

const plugin: PluginDefinition = {
  id: "github",
  version: "1.0.0",
  displayName: "GitHub",
  description: "Repositories, issues, and pull requests.",
  tags: ["code", "vcs"],
  skills: [],
  connectors: [
    {
      id: "github-connector",
      displayName: "GitHub connector",
      description: "Connect to a GitHub account.",
      authSchemes: [
        {
          id: "oauth",
          displayName: "OAuth",
          type: "OAuth2",
          installationFields: [],
          connectionFields: [],
          installation: { enabled: true },
        },
      ],
      capabilitySources: [],
    },
  ],
};

function renderCard(
  canConfigureInstallation: boolean,
  handlers: { configured?: unknown[]; created?: unknown[] } = {},
) {
  return render(
    React.createElement(PluginCard, {
      canConfigureInstallation,
      plugin,
      onConfigure: (selection: IntegrationSelection) => handlers.configured?.push(selection),
      onCreateConnection: (selection: IntegrationSelection) => handlers.created?.push(selection),
    }),
  );
}

test("the card shows its display name, version, connector, and auth scheme", () => {
  renderCard(false);

  assert.ok(screen.getByText("GitHub"));
  assert.ok(screen.getByText("v1.0.0"));
  assert.ok(screen.getByText("GitHub connector"));
  assert.ok(screen.getByText("OAuth"));
  assert.ok(screen.getByText("Ready"));
});

test("the Configure action appears only when the user can configure the installation", () => {
  const view = renderCard(false);
  assert.equal(screen.queryByRole("button", { name: "Configure" }), null);
  view.unmount();

  renderCard(true);
  assert.ok(screen.getByRole("button", { name: "Configure" }));
});

test("Configure and New integration report the connector and auth scheme", () => {
  const configured: unknown[] = [];
  const created: unknown[] = [];
  renderCard(true, { configured, created });

  fireEvent.click(screen.getByRole("button", { name: "Configure" }));
  fireEvent.click(screen.getByRole("button", { name: "New integration" }));

  const expected = {
    plugin,
    connector: plugin.connectors[0],
    authScheme: plugin.connectors[0].authSchemes[0],
  };
  assert.deepEqual(configured, [expected]);
  assert.deepEqual(created, [expected]);
});
