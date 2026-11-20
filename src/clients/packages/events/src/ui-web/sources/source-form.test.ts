import assert from "node:assert/strict";
import test from "node:test";
import type { EventSourceResponse } from "@agw/api";

import {
  createSourceForm,
  sourceFormToRequest,
  sourceToForm,
  validateSourceForm,
  type SourceFormOptions,
} from "./source-form";

const FORM_OPTIONS: SourceFormOptions = {
  requiresConfiguration: false,
  httpPoll: { maxPages: 20, cursorPathValid: true },
};

test("Webhook with a fixed type needs only a name and type", () => {
  const form = createSourceForm(1);
  form.nameSuffix = "demo-ticket";
  form.typeMode = "fixed";
  form.fixedType = "demo.ticket.created";

  assert.deepEqual(sourceFormToRequest(form, FORM_OPTIONS), {
    name: "webhook.demo-ticket",
    config: { fixedType: "demo.ticket.created" },
  });
  assert.deepEqual(validateSourceForm(form, FORM_OPTIONS), []);
});

test("a blank Webhook token header uses the default and a custom name is trimmed", () => {
  const form = createSourceForm(1);
  form.nameSuffix = "demo-ticket";
  form.typeMode = "fixed";
  form.fixedType = "demo.ticket.created";

  for (const header of ["", "  ", " Authorization "]) {
    form.tokenHeader = header;
    assert.deepEqual(sourceFormToRequest(form, FORM_OPTIONS).config, {
      fixedType: "demo.ticket.created",
    });
  }
  form.tokenHeader = " X-Webhook-Token ";
  assert.deepEqual(sourceFormToRequest(form, FORM_OPTIONS).config, {
    fixedType: "demo.ticket.created",
    auth: { tokenHeader: "X-Webhook-Token" },
  });
});

test("HTTP poll authentication builds secrets and preserves an empty edit secret", () => {
  const form = createSourceForm(2);
  form.nameSuffix = "orders";
  form.eventTypes = "shop.order.*";
  form.url = "https://example.test/orders";
  form.authKind = "basic";
  form.basicUsername = "reader";
  form.basicPassword = "private";
  assert.deepEqual(sourceFormToRequest(form, FORM_OPTIONS).secret, {
    username: "reader",
    password: "private",
  });

  form.authKind = "header";
  form.headerName = "X-Token";
  form.headerValue = "private";
  const header = sourceFormToRequest(form, FORM_OPTIONS);
  assert.deepEqual(header.secret, { value: "private" });
  assert.deepEqual((header.config as { request: { auth: unknown } }).request.auth, {
    kind: "header",
    headerName: "X-Token",
  });

  form.authKind = "bearer";
  form.bearerToken = "private";
  assert.equal(sourceFormToRequest(form, FORM_OPTIONS).secret, "private");
  form.savedSecretKind = "bearer";
  form.bearerToken = "";
  assert.equal("secret" in sourceFormToRequest(form, FORM_OPTIONS), false);
});

test("changing HTTP poll authentication requires the matching secret", () => {
  const form = createSourceForm(2);
  form.nameSuffix = "orders";
  form.eventTypes = "shop.order.*";
  form.url = "https://example.test/orders";
  form.authKind = "bearer";

  assert.ok(validateSourceForm(form, FORM_OPTIONS).some((error) => error.includes("Bearer Token")));
  form.bearerToken = "new-token";
  assert.deepEqual(validateSourceForm(form, FORM_OPTIONS), []);

  form.savedSecretKind = "bearer";
  form.authKind = "header";
  form.headerName = "X-Token";
  form.bearerToken = "";
  assert.ok(validateSourceForm(form, FORM_OPTIONS).some((error) => error.includes("header value")));
  form.headerValue = "new-token";
  assert.deepEqual(validateSourceForm(form, FORM_OPTIONS), []);
});

test("editing a source without a saved token requires a new Bearer Token", () => {
  const source: EventSourceResponse = {
    id: "source-1",
    name: "http.orders",
    kind: 2,
    managedBy: "user",
    ceSource: "/agw/sources/source-1",
    config: {
      eventTypes: ["shop.order.*"],
      request: { url: "https://example.test/orders", auth: { kind: "none" } },
      interval: "PT5M",
    },
    eventTypes: ["shop.order.*"],
    enabled: true,
    hasSecret: false,
    version: 1,
  };
  const form = sourceToForm(source);
  form.authKind = "bearer";

  assert.ok(validateSourceForm(form, FORM_OPTIONS).some((error) => error.includes("Bearer Token")));
  form.bearerToken = "new-token";
  assert.deepEqual(validateSourceForm(form, FORM_OPTIONS), []);
});

test("Webhook clock skew requires a timestamp header", () => {
  const form = createSourceForm(1);
  form.nameSuffix = "orders";
  form.typeMode = "fixed";
  form.fixedType = "shop.order.created";
  form.webhookAuthKind = "HmacSha256";
  form.maxClockSkewSeconds = "300";

  assert.ok(
    validateSourceForm(form, FORM_OPTIONS).some((error) => error.includes("Timestamp header")),
  );
  form.timestampHeader = "X-Timestamp";
  assert.deepEqual(validateSourceForm(form, FORM_OPTIONS), []);
  form.maxClockSkewSeconds = "3601";
  assert.ok(validateSourceForm(form, FORM_OPTIONS).some((error) => error.includes("3600")));
});

test("CloudEvents HTTP poll cannot use cursor pagination", () => {
  const form = createSourceForm(2);
  form.nameSuffix = "orders";
  form.eventTypes = "shop.order.*";
  form.url = "https://example.test/orders";
  form.format = "cloudevents";
  form.paginationKind = "cursor";
  form.cursorPath = "$.next";
  form.cursorParameter = "cursor";

  assert.ok(
    validateSourceForm(form, FORM_OPTIONS).some((error) => error.includes("Cursor pagination")),
  );
});

test("item comparison requires both cursor pagination parameters", () => {
  const form = createSourceForm(2);
  form.nameSuffix = "orders";
  form.eventTypes = "shop.order.*";
  form.url = "https://example.test/orders";
  form.format = "mapped";
  form.mappingText = '{"items":"$.items[*]","key":{"var":"item.id"}}';
  form.paginationKind = "cursor";
  form.cursorPath = "  ";
  form.cursorParameter = "cursor";

  assert.throws(() => sourceFormToRequest(form, FORM_OPTIONS), /cursor path/i);
  form.cursorPath = "$.next";
  form.cursorParameter = "  ";
  assert.throws(() => sourceFormToRequest(form, FORM_OPTIONS), /cursor parameter/i);
  form.cursorParameter = " cursor ";
  assert.deepEqual(
    (sourceFormToRequest(form, FORM_OPTIONS).config as { pagination: unknown }).pagination,
    {
      kind: "cursor",
      cursorPath: "$.next",
      cursorParameter: "cursor",
    },
  );
});

test("HTTP pagination accepts an omitted page limit or a positive integer", () => {
  const form = createSourceForm(2);
  form.nameSuffix = "orders";
  form.eventTypes = "shop.order.*";
  form.url = "https://example.test/orders";
  form.paginationKind = "page";

  for (const value of ["0", "-1", "1.5", "2147483648"]) {
    form.maxPages = value;
    assert.throws(() => sourceFormToRequest(form, FORM_OPTIONS), /maximum pages/i);
  }
  form.maxPages = "2";
  form.pageParameter = " page ";
  assert.deepEqual(
    (sourceFormToRequest(form, FORM_OPTIONS).config as { pagination: unknown }).pagination,
    {
      kind: "page",
      maxPages: 2,
      pageParameter: "page",
    },
  );
  form.maxPages = "";
  form.pageParameter = " ";
  assert.deepEqual(
    (sourceFormToRequest(form, FORM_OPTIONS).config as { pagination: unknown }).pagination,
    {
      kind: "page",
    },
  );
});

test("HTTP pagination requires server settings even when maximum pages is omitted", () => {
  const form = createSourceForm(2);
  form.nameSuffix = "orders";
  form.eventTypes = "shop.order.*";
  form.url = "https://example.test/orders";
  form.maxPages = "";
  const options = { requiresConfiguration: false };

  for (const kind of ["page", "linkHeader"] as const) {
    form.paginationKind = kind;
    assert.throws(() => sourceFormToRequest(form, options), /pagination settings/i);
    assert.deepEqual(validateSourceForm(form, FORM_OPTIONS), []);
  }
});

test("HTTP pagination validates against the server limit and path parser", () => {
  const form = createSourceForm(2);
  form.nameSuffix = "orders";
  form.eventTypes = "shop.order.*";
  form.url = "https://example.test/orders";
  form.format = "mapped";
  form.mappingText = '{"items":"$.items[*]","key":{"var":"item.id"}}';
  form.paginationKind = "cursor";
  form.maxPages = "5";
  form.cursorPath = "invalid-path";
  form.cursorParameter = "cursor";
  const options = {
    requiresConfiguration: false,
    httpPoll: { maxPages: 4, cursorPathValid: false },
  };

  const errors = validateSourceForm(form, options);

  assert.ok(errors.some((error) => error.includes("4")));
  assert.ok(errors.some((error) => error.includes("valid JSONPath")));
  form.maxPages = "4";
  form.cursorPath = "$.next";
  assert.deepEqual(
    validateSourceForm(form, { ...options, httpPoll: { maxPages: 4, cursorPathValid: true } }),
    [],
  );
});

test("mapped response parses mapping JSON and reports invalid JSON", () => {
  const form = createSourceForm(2);
  form.nameSuffix = "orders";
  form.eventTypes = "shop.order.*";
  form.url = "https://example.test/orders";
  form.format = "mapped";
  form.mappingText = '{"items":"$.items[*]"}';
  assert.deepEqual(
    (sourceFormToRequest(form, FORM_OPTIONS).config as { mapping: unknown }).mapping,
    {
      items: "$.items[*]",
    },
  );
  form.mappingText = "{";
  assert.ok(validateSourceForm(form, FORM_OPTIONS).some((error) => error.includes("Mapping")));
});

test("HTTP request headers are built from name and value rows", () => {
  const form = createSourceForm(2);
  form.nameSuffix = "orders";
  form.eventTypes = "shop.order.*";
  form.url = "https://example.test/orders";
  form.headers = [{ name: "X-Client", value: "agw" }];
  assert.deepEqual(
    (sourceFormToRequest(form, FORM_OPTIONS).config as { request: { headers: unknown } }).request
      .headers,
    {
      "X-Client": "agw",
    },
  );
});

test("unrepresentable saved interval requires a new selection", () => {
  const source: EventSourceResponse = {
    id: "source-1",
    name: "http.orders",
    kind: 2,
    managedBy: "user",
    ceSource: "/agw/sources/source-1",
    config: {
      eventTypes: ["shop.order.*"],
      request: { url: "https://example.test/orders" },
      interval: "PT90S",
    },
    eventTypes: ["shop.order.*"],
    enabled: true,
    hasSecret: false,
    version: 1,
  };

  const form = sourceToForm(source);

  assert.equal(form.intervalValue, "");
  assert.equal(form.originalInterval, "PT90S");
  assert.ok(validateSourceForm(form, FORM_OPTIONS).some((error) => error.includes("interval")));
});

test("editing unrelated fields preserves commas in saved event types and content types", () => {
  const source: EventSourceResponse = {
    id: "source-1",
    name: "webhook.orders",
    kind: 1,
    managedBy: "user",
    ceSource: "/agw/sources/source-1",
    config: {
      eventTypes: ["shop.order,created"],
      contentTypes: ['application/json; profile="a,b"'],
    },
    eventTypes: ["shop.order,created"],
    enabled: true,
    hasSecret: true,
    version: 1,
  };
  const form = sourceToForm(source);
  form.nameSuffix = "new-orders";

  assert.deepEqual(sourceFormToRequest(form, FORM_OPTIONS).config, {
    eventTypes: ["shop.order,created"],
    contentTypes: ['application/json; profile="a,b"'],
  });
});

test("switching Webhook authentication omits inactive HMAC settings", () => {
  const form = createSourceForm(1);
  form.nameSuffix = "orders";
  form.typeMode = "fixed";
  form.fixedType = "shop.order.created";
  form.webhookAuthKind = "HmacSha256";
  form.signatureHeader = "X-Signature";
  form.timestampHeader = "X-Timestamp";
  form.maxClockSkewSeconds = "300";
  form.webhookAuthKind = "BearerToken";

  assert.deepEqual(sourceFormToRequest(form, FORM_OPTIONS).config, {
    fixedType: "shop.order.created",
  });
});

test("sourceToForm rejects unsupported saved configuration values", () => {
  const source: EventSourceResponse = {
    id: "source-1",
    name: "http.orders",
    kind: 2,
    managedBy: "user",
    ceSource: "/agw/sources/source-1",
    config: {
      eventTypes: ["shop.order.*"],
      request: { url: "https://example.test/orders", method: "DELETE" },
      interval: "PT5M",
    },
    eventTypes: ["shop.order.*"],
    enabled: true,
    hasSecret: false,
    version: 1,
  };

  assert.throws(() => sourceToForm(source), /method/);
  source.config = {
    eventTypes: ["shop.order.*"],
    request: { url: "https://example.test/orders" },
    interval: "PT5M",
    format: "xml",
  };
  assert.throws(() => sourceToForm(source), /format/);
  source.config = {
    eventTypes: ["shop.order.*"],
    format: "cloudevents",
    request: { url: "https://example.test/orders", auth: { kind: "oauth" } },
    interval: "PT5M",
  };
  assert.throws(() => sourceToForm(source), /authentication/);
  source.config = {
    eventTypes: ["shop.order.*"],
    request: { url: "https://example.test/orders" },
    interval: "PT5M",
    pagination: { kind: "offset" },
  };
  assert.throws(() => sourceToForm(source), /pagination/);
  source.config = {
    eventTypes: ["shop.order.*", 42],
    request: { url: "https://example.test/orders" },
    interval: "PT5M",
  };
  assert.throws(() => sourceToForm(source), /event types/);
  source.config = {
    eventTypes: ["shop.order.*"],
    request: { url: 42 },
    interval: "PT5M",
  };
  assert.throws(() => sourceToForm(source), /configuration text/);
});

test("sourceToForm rejects saved pagination and Webhook fields with the wrong types", () => {
  const source: EventSourceResponse = {
    id: "source-1",
    name: "http.orders",
    kind: 2,
    managedBy: "user",
    ceSource: "/agw/sources/source-1",
    config: {
      eventTypes: ["shop.order.*"],
      request: { url: "https://example.test/orders" },
      interval: "PT5M",
      pagination: { kind: "page", maxPages: "3" },
    },
    eventTypes: ["shop.order.*"],
    enabled: true,
    hasSecret: false,
    version: 1,
  };
  assert.throws(() => sourceToForm(source), /maximum pages/i);

  const webhook = { ...source, name: "webhook.orders", kind: 1 };
  webhook.config = {
    eventTypes: ["shop.order.*"],
    auth: { kind: "HmacSha256", timestampHeader: "X-Timestamp", maxClockSkewSeconds: "60" },
  };
  assert.throws(() => sourceToForm(webhook), /max clock skew/i);
  webhook.config = { eventTypes: ["shop.order.*"], contentTypes: ["application/json", 1] };
  assert.throws(() => sourceToForm(webhook), /content types/i);
});
