import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";
import {
  DesktopOidcLogin,
  parseDesktopAuthRedirect,
  type OidcLoginDependencies,
} from "./oidc-login";
import type { ServerProfile } from "../shared/contracts";

function fixture() {
  let profile: ServerProfile = {
    id: "remote-1",
    kind: "remote",
    name: "Office",
    baseUrl: "https://agw.example",
    apiMajorVersion: 1,
    allowInsecureHttp: false,
  };
  let opened: (url: URL) => void = () => {};
  const browser = new Promise<URL>((resolve) => {
    opened = resolve;
  });
  const exchanges: RequestInit[] = [];
  const saves: string[] = [];
  let revocations = 0;
  let removed = false;
  let failSave = false;
  let failLogout = false;
  const dependencies: OidcLoginDependencies = {
    getProfile: async (id) => {
      assert.equal(id, profile.id);
      return profile;
    },
    openExternal: async (url) => {
      opened(new URL(url));
    },
    fetch: async (url, init) => {
      assert.equal(init.credentials, "omit");
      assert.equal(init.redirect, "error");
      if (url.endsWith("/exchange")) {
        exchanges.push(init);
        return Response.json({ code: 0, data: { token: "agw_test-secret", userId: "10000" } });
      }
      if (url.endsWith("/logout")) {
        revocations++;
        if (failLogout) throw new Error("Offline");
        return Response.json({ code: 0 });
      }
      return Response.json({ code: 0, data: [{ id: "company", displayName: "Company" }] });
    },
    saveToken: async (_id, token, beforeCommit) => {
      await beforeCommit();
      if (failSave) throw new Error("Storage failed");
      saves.push(token);
    },
    loadToken: async () => "agw_saved-token",
    deleteToken: async () => {
      removed = true;
    },
  };
  const flow = new DesktopOidcLogin(dependencies);
  return {
    flow,
    browser,
    exchanges,
    saves,
    setProfile: (next: ServerProfile) => {
      profile = next;
    },
    profile: () => profile,
    failSave: () => {
      failSave = true;
    },
    failLogout: () => {
      failLogout = true;
    },
    revocations: () => revocations,
    removed: () => removed,
  };
}

function callback(start: URL, state = start.searchParams.get("clientState")!): string {
  return "agw-desktop://auth/complete?" + new URLSearchParams({ state, code: "A".repeat(43) });
}

test("Desktop exchanges only a local handoff code with its own verifier, saves once", async () => {
  const f = fixture();
  const completion = f.flow.login("remote-1", "company");
  const start = await f.browser;
  assert.equal(start.origin, "https://agw.example");
  assert.equal(start.searchParams.get("codeChallengeMethod"), "S256");
  assert.equal(start.searchParams.has("codeVerifier"), false);
  assert.equal(start.searchParams.has("clientSecret"), false);
  const redirect = callback(start);
  assert.equal(f.flow.handleRedirect(redirect), true);
  assert.equal(f.flow.handleRedirect(redirect), true);
  await completion;
  assert.equal(f.exchanges.length, 1);
  const request = JSON.parse(String(f.exchanges[0]!.body)) as {
    code: string;
    codeVerifier: string;
  };
  assert.equal(request.code, "A".repeat(43));
  assert.equal(
    createHash("sha256").update(request.codeVerifier).digest("base64url"),
    start.searchParams.get("codeChallenge"),
  );
  assert.deepEqual(f.saves, ["agw_test-secret"]);
  f.flow.handleRedirect(redirect);
  assert.equal(f.exchanges.length, 1);
});

test("wrong state cannot consume or overwrite a pending login", async () => {
  const f = fixture();
  const completion = f.flow.login("remote-1", "company");
  const rejected = assert.rejects(completion, /cancelled/u);
  const start = await f.browser;
  f.flow.handleRedirect(callback(start, "B".repeat(43)));
  assert.equal(f.exchanges.length, 0);
  f.flow.cancel("remote-1");
  await rejected;
  assert.deepEqual(f.saves, []);
});

test("changed Server address rejects a callback before any credential is exchanged", async () => {
  const f = fixture();
  const completion = f.flow.login("remote-1", "company");
  const rejected = assert.rejects(completion, /address changed/u);
  const start = await f.browser;
  f.setProfile({ ...f.profile(), baseUrl: "https://different.example" });
  f.flow.handleRedirect(callback(start));
  await rejected;
  assert.equal(f.exchanges.length, 0);
  assert.deepEqual(f.saves, []);
});

test("failed secure save preserves old credentials and revokes the unused new token", async () => {
  const f = fixture();
  f.failSave();
  const completion = f.flow.login("remote-1", "company");
  const rejected = assert.rejects(completion, /Storage failed/u);
  const start = await f.browser;
  f.flow.handleRedirect(callback(start));
  await rejected;
  assert.deepEqual(f.saves, []);
  assert.equal(f.revocations(), 1);
});

test("offline logout clears local credentials and reports unconfirmed revocation", async () => {
  const f = fixture();
  f.failLogout();
  await assert.rejects(f.flow.logout("remote-1"), /revocation was not confirmed/u);
  assert.equal(f.removed(), true);
});

test("redirect parsing rejects credentials, fragments, duplicate fields and other protocols", () => {
  const state = "A".repeat(43);
  const code = "B".repeat(43);
  for (const uri of [
    "https://auth/complete?state=" + state + "&code=" + code,
    "agw-desktop://user@auth/complete?state=" + state + "&code=" + code,
    "agw-desktop://auth/complete?state=" + state + "&state=" + state + "&code=" + code,
    "agw-desktop://auth/complete?state=" + state + "&code=" + code + "#fragment",
    "agw-desktop://auth/complete?state=" + state + "&code=" + code + "&server=https://evil.example",
    "agw-desktop://auth/complete?state=" + state + "&code=" + code + "&error=cancelled",
    "agw-desktop://oauth/complete?oauth=authorized",
  ])
    assert.equal(parseDesktopAuthRedirect(uri), null);
});

test("OIDC rejects an insecure remote origin even if manual-token HTTP consent is set", async () => {
  const f = fixture();
  f.setProfile({ ...f.profile(), baseUrl: "http://remote.example", allowInsecureHttp: true });
  await assert.rejects(f.flow.login("remote-1", "company"), /HTTPS/u);
});
