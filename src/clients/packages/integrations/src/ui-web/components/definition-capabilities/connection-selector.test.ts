import assert from "node:assert/strict";
import test from "node:test";

import {
  buildConnectionSelectOptions,
  buildSelectedConnectionItems,
} from "./connection-selector.ts";

const connections = [
  {
    id: "ready",
    alias: "github_work",
    displayName: "GitHub Work",
    pluginId: "github",
    connectorId: "github-cloud",
    authSchemeId: "oauth2",
    status: "Ready",
    subject: "octocat",
  },
  {
    id: "expired",
    alias: "github_personal",
    displayName: "GitHub Personal",
    pluginId: "github",
    connectorId: "github-cloud",
    authSchemeId: "oauth2",
    status: "Expired",
    subject: null,
  },
] as const;

test("connection options only offer ready connections for a new binding", () => {
  assert.deepEqual(
    buildConnectionSelectOptions(connections, []).map((option) => option.value),
    ["ready"],
  );
});

test("an existing non-ready binding remains visible and removable", () => {
  const options = buildConnectionSelectOptions(connections, ["expired"]);
  assert.deepEqual(
    options.map((option) => option.value),
    ["ready", "expired"],
  );
  assert.match(options[1].subtitle ?? "", /Expired/);

  assert.deepEqual(buildSelectedConnectionItems(["expired"], connections), [
    {
      id: "expired",
      title: "GitHub Personal · github_personal",
      description: "github-cloud · Expired",
    },
  ]);
});
