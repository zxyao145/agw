import assert from "node:assert/strict";
import test from "node:test";

import type { ExtensionManifest } from "@agw/extension-sdk";
import { PERMISSION_DESCRIPTIONS } from "../../shared/extension-permission-descriptions";
import { permissionApprovalView } from "./permission-approval";

const manifest: ExtensionManifest = {
  manifestVersion: 1,
  id: "sample",
  name: "Sample",
  version: "1.2.0",
  permissions: ["fs.local.read", "execution", "clipboard.write"],
  hostPermissions: ["https://api.example.com", "http://intranet.example.com"],
};

test("the approval lists the app's descriptions, risks, network access and notes", () => {
  assert.deepEqual(permissionApprovalView("enable", manifest, null), {
    kind: "enable",
    extensionId: "sample",
    extensionName: "Sample",
    version: "1.2.0",
    installedVersion: null,
    downgrade: false,
    permissions: [
      {
        name: "fs.local.read",
        description: PERMISSION_DESCRIPTIONS["fs.local.read"],
        highRisk: true,
      },
      { name: "execution", description: PERMISSION_DESCRIPTIONS.execution, highRisk: true },
      {
        name: "clipboard.write",
        description: PERMISSION_DESCRIPTIONS["clipboard.write"],
        highRisk: false,
      },
    ],
    hostPermissions: [
      { pattern: "https://api.example.com", unencrypted: false },
      { pattern: "http://intranet.example.com", unencrypted: true },
    ],
    sendsData: true,
    runsAgents: true,
  });
});

test("an install over a newer version is marked as a downgrade", () => {
  const view = permissionApprovalView(
    "install",
    { ...manifest, permissions: [], hostPermissions: [] },
    "2.0.0",
  );
  assert.equal(view.installedVersion, "2.0.0");
  assert.equal(view.downgrade, true);
  assert.equal(view.sendsData, false);
  assert.equal(permissionApprovalView("install", manifest, "1.0.0").downgrade, false);
});
