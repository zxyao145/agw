import assert from "node:assert/strict";
import test from "node:test";

import {
  classifyDesktopConnection,
  getActiveServerProfile,
  getEffectiveActiveServerProfile,
  type DesktopSettings,
} from "./runtime-model";

const settings: DesktopSettings = {
  schemaVersion: 1,
  packageFlavor: "full",
  closeBehavior: "minimize-to-tray",
  activeServerId: "local",
  projectTabsByServer: {},
  profiles: [
    {
      id: "local",
      kind: "local",
      name: "Local",
      baseUrl: "http://127.0.0.1:30815",
      apiMajorVersion: 1,
      allowInsecureHttp: true,
    },
  ],
};

test("getActiveServerProfile resolves the selected profile", () => {
  assert.equal(getActiveServerProfile(settings).id, "local");
});

test("classifyDesktopConnection requires setup, compatibility, and a token", () => {
  const profile = getActiveServerProfile(settings);

  assert.equal(
    classifyDesktopConnection(
      profile,
      { serverVersion: "1.0.0", apiMajorVersion: 1, initialized: false },
      null,
    ),
    "setup-required",
  );
  assert.equal(
    classifyDesktopConnection(
      profile,
      { serverVersion: "2.0.0", apiMajorVersion: 2, initialized: true },
      "agw_token",
    ),
    "incompatible",
  );
  assert.equal(
    classifyDesktopConnection(
      profile,
      { serverVersion: "1.0.0", apiMajorVersion: 1, initialized: true },
      null,
    ),
    "authentication-required",
  );
  assert.equal(
    classifyDesktopConnection(
      profile,
      { serverVersion: "1.0.0", apiMajorVersion: 1, initialized: true },
      "agw_token",
    ),
    "ready",
  );
});

test("getEffectiveActiveServerProfile follows the bundled Server runtime port", () => {
  const profile = getEffectiveActiveServerProfile({
    isDesktop: true,
    platform: "darwin",
    packageFlavor: "full",
    activeToken: null,
    settings,
    localServerRuntime: {
      schemaVersion: 1,
      pid: 42,
      baseUrl: "http://127.0.0.1:43123",
      port: 43123,
      serverVersion: "1.0.0",
      apiMajorVersion: 1,
      startedAt: "2026-07-17T00:00:00Z",
    },
  });

  assert.equal(profile.baseUrl, "http://127.0.0.1:43123");
});
