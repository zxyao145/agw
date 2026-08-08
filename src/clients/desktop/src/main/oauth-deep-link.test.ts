import assert from "node:assert/strict";
import test from "node:test";

import { findOAuthDeepLink, parseOAuthDeepLink } from "./oauth-deep-link";

test("parseOAuthDeepLink routes successful OAuth completion to Integrations", () => {
  assert.equal(
    parseOAuthDeepLink("agw-desktop://oauth/complete?oauth=authorized"),
    "/integrations/?oauth=authorized",
  );
});

test("parseOAuthDeepLink preserves known OAuth failures on the Integrations route", () => {
  assert.equal(
    parseOAuthDeepLink("agw-desktop://oauth/complete?oauth=error&code=token_exchange_failed"),
    "/integrations/?oauth=error&code=token_exchange_failed",
  );
});

test("parseOAuthDeepLink rejects other hosts and paths", () => {
  assert.equal(parseOAuthDeepLink("agw-desktop://app/integrations/?oauth=authorized"), null);
  assert.equal(parseOAuthDeepLink("agw-desktop://oauth/other?oauth=authorized"), null);
  assert.equal(parseOAuthDeepLink("https://example.com/oauth/complete?oauth=authorized"), null);
});

test("parseOAuthDeepLink normalizes unknown errors without accepting navigation input", () => {
  assert.equal(
    parseOAuthDeepLink(
      "agw-desktop://oauth/complete?oauth=error&code=unexpected&returnPath=/settings/",
    ),
    "/integrations/?oauth=error&code=invalid_state",
  );
});

test("findOAuthDeepLink extracts the protocol argument from a second-instance argv", () => {
  assert.equal(
    findOAuthDeepLink(["agw-desktop", "--flag", "agw-desktop://oauth/complete?oauth=authorized"]),
    "agw-desktop://oauth/complete?oauth=authorized",
  );
});
