import assert from "node:assert/strict";
import test from "node:test";

import {
  getA2ACardUrlError,
  getA2ACredentialGroups,
  getA2ACredentialsError,
  isA2AOriginWarningVisible,
  normalizeA2AOrigin,
  toA2AEndpointRequest,
  type A2ACredentialHeaderRow,
} from "./a2a-endpoint";

const cardOrigin = "https://card.example:443";
const cardUrl = "https://CARD.example/.well-known/agent-card.json";
const interfaceOrigin = "http://interface.example:8090";
const saved = { origin: cardOrigin, name: "Authorization", value: "", saved: true };
const tenant = { origin: cardOrigin, name: "X-Tenant", value: "test", saved: false };

test("card URLs must be absolute HTTP or HTTPS addresses without user info", () => {
  assert.equal(getA2ACardUrlError(cardUrl), null);
  for (const url of [
    "ftp://a.example/x",
    "/x",
    "https://u:p@a.example/x",
    "https://@a.example/x",
    "https://:@a.example/x",
    "https:a.example/x",
    "",
    "https://a.example/" + "x".repeat(2048),
  ]) {
    assert.ok(getA2ACardUrlError(url), url);
  }
});

test("origins are normalized with an effective port", () => {
  assert.equal(normalizeA2AOrigin("HTTPS://CARD.Example:443/"), cardOrigin);
  assert.equal(normalizeA2AOrigin("https://card.example"), cardOrigin);
  assert.equal(normalizeA2AOrigin("http://interface.example:8090"), interfaceOrigin);
  assert.equal(normalizeA2AOrigin("https://münchen.example"), "https://xn--mnchen-3ya.example:443");
  assert.equal(normalizeA2AOrigin("http://[::1]"), "http://[::1]:80");
  for (const origin of [
    cardUrl,
    cardOrigin + "/x/..",
    cardOrigin + "?",
    cardOrigin + "#",
    "https://u@card.example",
    "ftp://card.example",
  ]) {
    assert.equal(normalizeA2AOrigin(origin), null, origin);
  }
});

test("credentials enforce origin and header validation", () => {
  const error = (rows: A2ACredentialHeaderRow[], origin = cardOrigin) =>
    getA2ACredentialsError([{ origin, rows }]);
  assert.equal(error([saved, tenant]), null);
  assert.ok(error([{ ...tenant, name: "Host" }]));
  assert.ok(error([{ ...tenant, name: "Invalid Name" }]));
  assert.ok(error([tenant, { ...tenant, name: "x-tenant" }]));
  assert.ok(error([{ ...tenant, value: "" }]));
  assert.ok(error([{ ...tenant, value: "one\ntwo" }]));
  assert.ok(error([{ ...tenant, value: "x".repeat(8193) }]));
  assert.ok(error([saved], interfaceOrigin));
  assert.ok(
    error(Array.from({ length: 21 }, (_, index) => ({ ...tenant, name: `X-Header-${index}` }))),
  );
  assert.ok(
    getA2ACredentialsError(
      [cardOrigin, interfaceOrigin, "http://third.example"].map((origin) => ({
        origin,
        rows: [{ ...tenant, origin }],
      })),
    ),
  );
});

test("request groups keep saved values by origin and send newly entered values", () => {
  assert.deepEqual(
    toA2AEndpointRequest({
      cardUrl,
      groups: [
        { origin: "https://CARD.example", rows: [saved, tenant] },
        {
          origin: interfaceOrigin,
          rows: [{ ...saved, origin: interfaceOrigin, saved: false, value: "Bearer peer" }],
        },
      ],
    }),
    {
      cardUrl,
      credentials: [
        { origin: cardOrigin, headers: { Authorization: null, "X-Tenant": "test" } },
        { origin: interfaceOrigin, headers: { Authorization: "Bearer peer" } },
      ],
    },
  );
});

test("the HTTP warning is shown for an origin with credential rows", () => {
  assert.equal(
    isA2AOriginWarningVisible(interfaceOrigin, [{ ...tenant, origin: interfaceOrigin }]),
    true,
  );
  assert.equal(isA2AOriginWarningVisible(interfaceOrigin, []), false);
  assert.equal(isA2AOriginWarningVisible(cardOrigin, [tenant]), false);
});

test("a preview at the card origin displays one credentials group", () => {
  const groups = [{ origin: cardOrigin, rows: [saved] }];
  assert.deepEqual(
    getA2ACredentialGroups(cardUrl, "https://card.example:443/agent", groups),
    groups,
  );
});

test("a preview at a new interface origin displays an independent credentials group", () => {
  const groups = [{ origin: cardOrigin, rows: [tenant] }];
  assert.deepEqual(getA2ACredentialGroups(cardUrl, "http://interface.example:8090/agent", groups), [
    ...groups,
    { origin: interfaceOrigin, rows: [] },
  ]);
});
