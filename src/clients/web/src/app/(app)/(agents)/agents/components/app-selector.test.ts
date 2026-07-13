import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

const require = createRequire(import.meta.url);
const { buildAppOptionLabel, filterAppOptions, getAppAuthorizationState } =
  require("../../../../../components/definition-capabilities/app-selector.ts") as typeof import("../../../../../components/definition-capabilities/app-selector");

const options = [
  {
    id: "1",
    appName: "github",
    displayName: "GitHub",
    provider: "GitHub OAuth App",
    clientId: "gh-client",
    isAuthorized: true,
    isAuthorizationExpired: false,
    authorizationSubject: "octocat",
  },
  {
    id: "2",
    appName: "google-workspace",
    displayName: "Google Workspace",
    provider: "Google OAuth App",
    clientId: "google-client",
    isAuthorized: false,
    isAuthorizationExpired: false,
    authorizationSubject: null,
  },
] as const;

test("filterAppOptions matches display name, provider, client id, and subject", () => {
  assert.equal(filterAppOptions(options, "octocat").length, 1);
  assert.equal(filterAppOptions(options, "google").length, 1);
  assert.equal(filterAppOptions(options, "client").length, 2);
});

test("getAppAuthorizationState prioritizes expired over authorized", () => {
  assert.equal(
    getAppAuthorizationState({ isAuthorized: true, isAuthorizationExpired: true }),
    "Expired",
  );
  assert.equal(
    getAppAuthorizationState({ isAuthorized: true, isAuthorizationExpired: false }),
    "Authorized",
  );
  assert.equal(
    getAppAuthorizationState({ isAuthorized: false, isAuthorizationExpired: false }),
    "Not authorized",
  );
});

test("buildAppOptionLabel includes display name and client id", () => {
  assert.equal(buildAppOptionLabel(options[0]), "GitHub · gh-client");
});
