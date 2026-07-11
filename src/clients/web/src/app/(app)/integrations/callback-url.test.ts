import assert from "node:assert/strict";
import test from "node:test";

import { buildOAuthServerCallbackUrl } from "./callback-url";

test("buildOAuthServerCallbackUrl prefers the current origin and appends the backend callback path", () => {
  assert.equal(
    buildOAuthServerCallbackUrl({
      apiBaseUrl: "https://backend.example.com",
      currentOrigin: "https://frontend.example.com",
    }),
    "https://frontend.example.com/api/integrations/oauth/callback",
  );
});

test("buildOAuthServerCallbackUrl falls back to configured api base url when current origin is unavailable", () => {
  assert.equal(
    buildOAuthServerCallbackUrl({
      apiBaseUrl: "https://backend.example.com",
    }),
    "https://backend.example.com/api/integrations/oauth/callback",
  );
});
