import assert from "node:assert/strict";
import test from "node:test";

import {
  DEFAULT_LOCAL_PROFILE,
  normalizeServerUrl,
  validateServerProfiles,
  type ServerProfile,
} from "./server-profiles";

test("default local profile uses the locked loopback endpoint", () => {
  assert.deepEqual(DEFAULT_LOCAL_PROFILE, {
    id: "local",
    kind: "local",
    name: "Local",
    baseUrl: "http://127.0.0.1:30816",
    apiMajorVersion: 1,
    allowInsecureHttp: true,
  });
});

test("normalizeServerUrl removes trailing slashes and rejects non-http protocols", () => {
  assert.equal(normalizeServerUrl(" https://agw.example.test/// "), "https://agw.example.test");
  assert.throws(() => normalizeServerUrl("file:///tmp/agw"), /HTTP or HTTPS/);
});

test("validateServerProfiles allows one local and multiple remote profiles", () => {
  const remote: ServerProfile = {
    id: "remote-1",
    kind: "remote",
    name: "Office",
    baseUrl: "https://agw.example.test",
    apiMajorVersion: 1,
    allowInsecureHttp: false,
  };

  assert.doesNotThrow(() => validateServerProfiles([DEFAULT_LOCAL_PROFILE, remote]));
  assert.doesNotThrow(() =>
    validateServerProfiles([
      DEFAULT_LOCAL_PROFILE,
      remote,
      { ...remote, id: "remote-2", baseUrl: "https://agw-2.example.test" },
    ]),
  );
  assert.throws(
    () => validateServerProfiles([DEFAULT_LOCAL_PROFILE, remote, { ...remote }]),
    /non-empty and unique/,
  );
});

test("validateServerProfiles requires explicit consent for remote HTTP", () => {
  assert.throws(
    () =>
      validateServerProfiles([
        DEFAULT_LOCAL_PROFILE,
        {
          id: "remote-1",
          kind: "remote",
          name: "Lab",
          baseUrl: "http://192.0.2.10:30815",
          apiMajorVersion: 1,
          allowInsecureHttp: false,
        },
      ]),
    /allowInsecureHttp/,
  );
});
