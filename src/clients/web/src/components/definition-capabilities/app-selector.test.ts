import assert from "node:assert/strict";
import test from "node:test";

const MODULE_URL = new URL("./app-selector.ts", import.meta.url);

async function importAppSelectorModule() {
  try {
    return await import(MODULE_URL.href);
  } catch (error) {
    assert.fail(`shared App option helpers are missing or invalid: ${String(error)}`);
  }
}

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

test("shared App labels and authorization states match the Agent language", async () => {
  const { buildAppOptionLabel, getAppAuthorizationState } = await importAppSelectorModule();

  assert.equal(buildAppOptionLabel(options[0]), "GitHub · gh-client");
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
