import {
  isValidSourceSuffix,
  MAX_SOURCE_NAME_LENGTH,
  type ApiResponse,
  type EventSourceCreateRequest,
  type EventSourceResponse,
  type JsonValue,
} from "@agw/api";

import { readCommaSeparatedValues, type CommaSeparatedInput } from "../comma-separated-values";
import { readJsonObject, readStringArray, tryParseJson } from "../json-values";
import { MANAGED_BY_USER } from "../managed-record";
import { fromIsoInterval, toIsoInterval, type PollIntervalUnit } from "./poll-interval";
import { EVENT_SOURCE_KIND, getSourceNamePrefix, splitSourceName } from "./source-name";

export type SourceTypeMode = "declared" | "fixed";
export const HTTP_AUTH_OPTIONS = {
  none: "None",
  bearer: "Bearer Token",
  basic: "Basic",
  header: "Request header",
  connection: "Connection",
} as const;
export const WEBHOOK_AUTH_OPTIONS = {
  BearerToken: "Bearer Token",
  HmacSha256: "HMAC-SHA256",
} as const;
export const SOURCE_FORMAT_OPTIONS = {
  cloudevents: "CloudEvents",
  mapped: "Item comparison",
} as const;
export const HTTP_METHOD_OPTIONS = { GET: "GET", POST: "POST" } as const;
export const PAGINATION_OPTIONS = {
  none: "None",
  linkHeader: "Link header",
  page: "Page",
  cursor: "Cursor",
} as const;

type HttpAuthKind = keyof typeof HTTP_AUTH_OPTIONS;
export type CreatedWebhook = { id: string; secret: string };

type HttpPollFormOptions = NonNullable<ApiResponse<"/api/events/sources/http-poll-options", "get">>;

export type SourceFormOptions = {
  requiresConfiguration: boolean;
  httpPoll?: HttpPollFormOptions;
};

export class InvalidSourceConfigurationError extends Error {}

export type SourceFormState = {
  kind: number;
  managedBy: string;
  nameSuffix: string;
  typeMode: SourceTypeMode;
  eventTypes: CommaSeparatedInput;
  fixedType: string;
  format: keyof typeof SOURCE_FORMAT_OPTIONS;
  mappingText: string;
  cloudEventText: string;
  webhookAuthKind: keyof typeof WEBHOOK_AUTH_OPTIONS;
  tokenHeader: string;
  signatureHeader: string;
  timestampHeader: string;
  maxClockSkewSeconds: string;
  contentTypes: CommaSeparatedInput;
  url: string;
  intervalValue: string;
  intervalUnit: PollIntervalUnit;
  originalInterval: string;
  authKind: HttpAuthKind;
  savedSecretKind: HttpAuthKind | null;
  bearerToken: string;
  basicUsername: string;
  basicPassword: string;
  headerName: string;
  headerValue: string;
  connectionId: string;
  method: keyof typeof HTTP_METHOD_OPTIONS;
  headers: { name: string; value: string }[];
  bodyText: string;
  paginationKind: keyof typeof PAGINATION_OPTIONS;
  maxPages: string;
  pageParameter: string;
  cursorPath: string;
  cursorParameter: string;
  providerExtensionId: string;
  providerId: string;
  configurationId: string;
};

export function createSourceForm(kind: number = EVENT_SOURCE_KIND.webhook): SourceFormState {
  return {
    kind,
    managedBy: MANAGED_BY_USER,
    nameSuffix: "",
    typeMode: "declared",
    eventTypes: "",
    fixedType: "",
    format: "cloudevents",
    mappingText: "",
    cloudEventText: "",
    webhookAuthKind: "BearerToken",
    tokenHeader: "Authorization",
    signatureHeader: "",
    timestampHeader: "",
    maxClockSkewSeconds: "",
    contentTypes: "",
    url: "",
    intervalValue: "5",
    intervalUnit: "minutes",
    originalInterval: "",
    authKind: "none",
    savedSecretKind: null,
    bearerToken: "",
    basicUsername: "",
    basicPassword: "",
    headerName: "",
    headerValue: "",
    connectionId: "",
    method: "GET",
    headers: [],
    bodyText: "",
    paginationKind: "none",
    maxPages: "",
    pageParameter: "",
    cursorPath: "",
    cursorParameter: "",
    providerExtensionId: "",
    providerId: "",
    configurationId: "",
  };
}

function readRecord(value: unknown): Record<string, unknown> {
  const record = readJsonObject(value);
  if (record) return record;
  throw new InvalidSourceConfigurationError("Source configuration must be a JSON object.");
}

function readOptionalRecord(value: unknown): Record<string, unknown> {
  return value === undefined || value === null ? {} : readRecord(value);
}

function readText(value: unknown): string {
  if (value === undefined || value === null) return "";
  if (typeof value === "string") return value;
  throw new InvalidSourceConfigurationError("Source configuration text must be a string.");
}

function readIntegerText(value: unknown, label: string): string {
  if (value === undefined || value === null) return "";
  if (typeof value === "number" && Number.isInteger(value)) return String(value);
  throw new InvalidSourceConfigurationError(`Saved ${label} must be an integer.`);
}

function readTextList(value: unknown, label: string): string[] {
  if (value === undefined || value === null) return [];
  const list = readStringArray(value);
  if (list) return list;
  throw new InvalidSourceConfigurationError(`Saved ${label} must be a string array.`);
}

function readOption<const T extends string>(
  value: unknown,
  allowed: Readonly<Record<T, string>>,
  defaultValue: T,
  label: string,
): T {
  if (value === undefined || value === null) return defaultValue;
  if (typeof value === "string" && Object.hasOwn(allowed, value)) return value as T;
  throw new InvalidSourceConfigurationError(`Unsupported source ${label}: ${String(value)}`);
}

function formatJson(value: unknown): string {
  return value === undefined || value === null ? "" : JSON.stringify(value, null, 2);
}

function parseJson(textValue: string): JsonValue {
  return JSON.parse(textValue) as JsonValue;
}

function parseObject(textValue: string, label: string): Record<string, JsonValue> {
  const value = readJsonObject(parseJson(textValue));
  if (value) return value;
  throw new Error(`${label} must be a JSON object.`);
}

function isValidType(value: string, allowPattern: boolean): boolean {
  const invalidType = !value || /\s/.test(value) || value.startsWith("agw.");
  if (invalidType) return false;
  const star = value.indexOf("*");
  return (
    star < 0 ||
    (allowPattern && value.endsWith(".*") && star === value.length - 1 && value.length > 2)
  );
}

function readDeclaredTypes(config: Record<string, unknown>): Partial<SourceFormState> {
  const eventTypes = readTextList(config.eventTypes, "event types");
  return {
    typeMode: typeof config.fixedType === "string" ? "fixed" : "declared",
    eventTypes,
    fixedType: readText(config.fixedType),
  };
}

function readWebhookFields(config: Record<string, unknown>): Partial<SourceFormState> {
  const auth = readOptionalRecord(config.auth);
  return {
    webhookAuthKind: readOption(
      auth.kind,
      WEBHOOK_AUTH_OPTIONS,
      "BearerToken",
      "Webhook authentication",
    ),
    tokenHeader: readText(auth.tokenHeader).trim() || "Authorization",
    signatureHeader: readText(auth.signatureHeader),
    timestampHeader: readText(auth.timestampHeader),
    maxClockSkewSeconds: readIntegerText(auth.maxClockSkewSeconds, "max clock skew"),
    contentTypes: readTextList(config.contentTypes, "content types"),
  };
}

function readHttpFields(
  config: Record<string, unknown>,
  hasSecret: boolean,
): Partial<SourceFormState> {
  const request = readOptionalRecord(config.request);
  const auth = readOptionalRecord(request.auth);
  const interval = readText(config.interval);
  const parsedInterval = fromIsoInterval(interval);
  const authKind = readOption(auth.kind, HTTP_AUTH_OPTIONS, "none", "HTTP authentication");
  return {
    url: readText(request.url),
    intervalValue: parsedInterval ? String(parsedInterval.value) : "",
    intervalUnit: parsedInterval?.unit ?? "minutes",
    originalInterval: interval,
    authKind,
    savedSecretKind: hasSecret ? authKind : null,
    headerName: readText(auth.headerName),
    connectionId: readText(auth.connectionId),
    method: readOption(request.method, HTTP_METHOD_OPTIONS, "GET", "method"),
    headers: Object.entries(readOptionalRecord(request.headers)).map(([name, value]) => ({
      name,
      value: readText(value),
    })),
    bodyText: formatJson(request.body),
  };
}

function readPaginationFields(config: Record<string, unknown>): Partial<SourceFormState> {
  const pagination = readOptionalRecord(config.pagination);
  return {
    paginationKind: readOption(pagination.kind, PAGINATION_OPTIONS, "none", "pagination"),
    maxPages: readIntegerText(pagination.maxPages, "maximum pages"),
    pageParameter: readText(pagination.pageParameter),
    cursorPath: readText(pagination.cursorPath),
    cursorParameter: readText(pagination.cursorParameter),
  };
}

export function sourceToForm(source: EventSourceResponse): SourceFormState {
  const config = readRecord(source.config);
  return {
    ...createSourceForm(source.kind),
    managedBy: source.managedBy,
    nameSuffix: splitSourceName(source.name, getSourceNamePrefix(source.kind, source.managedBy)),
    ...readDeclaredTypes(config),
    format: readOption(config.format, SOURCE_FORMAT_OPTIONS, "cloudevents", "format"),
    mappingText: formatJson(config.mapping),
    cloudEventText: formatJson(config.cloudEvent),
    ...readWebhookFields(config),
    ...readHttpFields(config, source.hasSecret),
    ...readPaginationFields(config),
    providerExtensionId: source.providerExtensionId ?? "",
    providerId: source.providerId ?? "",
    configurationId: readText(config.configurationId),
  };
}

function validateSourceName(form: SourceFormState): string[] {
  const prefix = getSourceNamePrefix(form.kind, form.managedBy);
  const validSuffix = isValidSourceSuffix(form.nameSuffix);
  const withinLimit = prefix.length + form.nameSuffix.length <= MAX_SOURCE_NAME_LENGTH;
  return validSuffix && withinLimit
    ? []
    : ["Name suffix must use lowercase letters, digits, '-' and '.', with no consecutive dots."];
}

function validateSourceTypes(form: SourceFormState): string[] {
  if (form.typeMode === "fixed")
    return isValidType(form.fixedType.trim(), false) ? [] : ["Enter a valid fixed event type."];
  const eventTypes = getDeclaredEventTypes(form.eventTypes);
  const validTypes =
    eventTypes.length > 0 && eventTypes.every((eventType) => isValidType(eventType, true));
  return validTypes ? [] : ["Enter at least one valid event type or prefix pattern."];
}

function getDeclaredEventTypes(eventTypes: SourceFormState["eventTypes"]): string[] {
  return readCommaSeparatedValues(eventTypes).map((eventType) => eventType.trim());
}

function validateSourceMapping(form: SourceFormState): string[] {
  const mapping = form.format === "mapped";
  const cloudEventDisplay =
    form.kind === EVENT_SOURCE_KIND.webhook && Boolean(form.cloudEventText.trim());
  if (!(mapping || cloudEventDisplay)) return [];
  const label = mapping ? "Mapping" : "CloudEvent display mapping";
  const value = tryParseJson(mapping ? form.mappingText : form.cloudEventText);
  if (value === undefined) return [`${label} must be valid JSON.`];
  return readJsonObject(value) ? [] : [`${label} must be a JSON object.`];
}

function validateHttpLocation(form: SourceFormState): string[] {
  const errors: string[] = [];
  if (URL.canParse(form.url)) {
    const url = new URL(form.url);
    const validUrl = ["http:", "https:"].includes(url.protocol) && !url.username && !url.password;
    if (!validUrl) errors.push("Enter an HTTP or HTTPS URL without credentials.");
  } else {
    errors.push("Enter an HTTP or HTTPS URL.");
  }
  const validInterval =
    /^[1-9]\d*$/.test(form.intervalValue) && Number.isSafeInteger(Number(form.intervalValue));
  if (!validInterval) errors.push("Choose a positive poll interval.");
  return errors;
}

function validateHttpAuthentication(form: SourceFormState): string[] {
  const errors: string[] = [];
  const needsNewSecret = form.savedSecretKind !== form.authKind;
  if (form.authKind === "connection" && !form.connectionId) errors.push("Select a connection.");
  if (form.authKind === "header" && !form.headerName.trim()) errors.push("Enter a header name.");
  if (form.authKind === "bearer" && needsNewSecret && !form.bearerToken)
    errors.push("Enter a Bearer Token.");
  if (form.authKind === "basic" && needsNewSecret && (!form.basicUsername || !form.basicPassword))
    errors.push("Enter a username and password.");
  if (form.authKind === "basic" && Boolean(form.basicUsername) !== Boolean(form.basicPassword))
    errors.push("Enter both username and password to change Basic authentication.");
  if (form.authKind === "header" && needsNewSecret && !form.headerValue)
    errors.push("Enter a header value.");
  return errors;
}

function validateHttpRequest(form: SourceFormState): string[] {
  const errors: string[] = [];
  if (form.headers.some((header) => !header.name.trim() || !header.value.trim()))
    errors.push("Request headers need a name and value.");
  const uniqueNames = new Set(form.headers.map((header) => header.name.trim().toLowerCase()));
  if (uniqueNames.size !== form.headers.length) errors.push("Request header names must be unique.");
  const hasBody = form.method === "POST" && Boolean(form.bodyText.trim());
  if (hasBody && tryParseJson(form.bodyText) === undefined)
    errors.push("Request body must be valid JSON.");
  return errors;
}

function validateHttpPagination(form: SourceFormState, options?: HttpPollFormOptions): string[] {
  if (form.paginationKind === "none") return [];
  if (options === undefined) return ["Waiting for pagination settings."];
  const errors: string[] = [];
  if (form.paginationKind === "cursor") {
    if (form.format === "cloudevents") {
      errors.push("Cursor pagination requires item comparison format.");
    } else {
      if (!form.cursorPath.trim()) errors.push("Enter a cursor path.");
      else if (options.cursorPathValid === false)
        errors.push("Enter a valid JSONPath for the cursor.");
      else if (options.cursorPathValid !== true) errors.push("Waiting for cursor path validation.");
      if (!form.cursorParameter.trim()) errors.push("Enter a cursor parameter.");
    }
  }
  if (form.maxPages.trim()) {
    const maxPages = Number(form.maxPages);
    if (!Number.isSafeInteger(maxPages) || maxPages < 1)
      errors.push("Maximum pages must be a positive whole number.");
    else if (maxPages > options.maxPages)
      errors.push(`Maximum pages must be between 1 and ${options.maxPages}.`);
  }
  return errors;
}

export function validateSourceForm(form: SourceFormState, options: SourceFormOptions): string[] {
  const errors = validateSourceName(form);
  if (form.kind === EVENT_SOURCE_KIND.provider) {
    if (!form.providerExtensionId.trim() || !form.providerId.trim())
      errors.push("Select a source provider.");
    if (options.requiresConfiguration && !form.configurationId)
      errors.push("Select a configuration.");
    return errors;
  }
  errors.push(...validateSourceTypes(form), ...validateSourceMapping(form));
  const hasHmacClockSkew =
    form.kind === EVENT_SOURCE_KIND.webhook &&
    form.webhookAuthKind === "HmacSha256" &&
    Boolean(form.maxClockSkewSeconds.trim());
  if (hasHmacClockSkew && !form.timestampHeader.trim()) {
    errors.push("Enter a Timestamp header when max clock skew is set.");
  }
  const invalidClockSkew =
    !/^\d+$/.test(form.maxClockSkewSeconds.trim()) || Number(form.maxClockSkewSeconds) > 3600;
  if (hasHmacClockSkew && invalidClockSkew) {
    errors.push("Max clock skew must be between 0 and 3600 seconds.");
  }
  if (form.kind === EVENT_SOURCE_KIND.httpPoll)
    errors.push(
      ...validateHttpLocation(form),
      ...validateHttpAuthentication(form),
      ...validateHttpRequest(form),
      ...validateHttpPagination(form, options.httpPoll),
    );
  return errors;
}

function buildSourceConfig(form: SourceFormState): Record<string, JsonValue> {
  const config: Record<string, JsonValue> =
    form.typeMode === "fixed"
      ? { fixedType: form.fixedType.trim() }
      : { eventTypes: getDeclaredEventTypes(form.eventTypes) };
  if (form.format === "mapped") {
    config.format = "mapped";
    config.mapping = parseObject(form.mappingText, "Mapping");
  } else if (form.kind === EVENT_SOURCE_KIND.webhook && form.cloudEventText.trim()) {
    config.cloudEvent = parseObject(form.cloudEventText, "CloudEvent display mapping");
  }
  return config;
}

function addWebhookConfig(form: SourceFormState, config: Record<string, JsonValue>): void {
  const auth: Record<string, JsonValue> = {};
  if (form.webhookAuthKind !== "BearerToken") auth.kind = form.webhookAuthKind;
  const tokenHeader = form.tokenHeader.trim();
  if (form.webhookAuthKind === "BearerToken" && tokenHeader && tokenHeader !== "Authorization")
    auth.tokenHeader = tokenHeader;
  if (form.webhookAuthKind === "HmacSha256") {
    if (form.signatureHeader.trim()) auth.signatureHeader = form.signatureHeader.trim();
    if (form.timestampHeader.trim()) auth.timestampHeader = form.timestampHeader.trim();
    if (form.maxClockSkewSeconds.trim())
      auth.maxClockSkewSeconds = Number(form.maxClockSkewSeconds);
  }
  if (Object.keys(auth).length > 0) config.auth = auth;
  const contentTypes = readCommaSeparatedValues(form.contentTypes);
  if (contentTypes.length > 0) config.contentTypes = contentTypes;
}

function addHttpConfig(form: SourceFormState, config: Record<string, JsonValue>): void {
  const request: Record<string, JsonValue> = { url: form.url.trim() };
  if (form.method !== "GET") request.method = form.method;
  if (form.headers.length > 0)
    request.headers = Object.fromEntries(
      form.headers.map((header) => [header.name.trim(), header.value]),
    );
  if (form.method === "POST" && form.bodyText.trim()) request.body = parseJson(form.bodyText);
  if (form.authKind !== "none") {
    request.auth = {
      kind: form.authKind,
      ...(form.authKind === "header" ? { headerName: form.headerName.trim() } : {}),
      ...(form.authKind === "connection" ? { connectionId: form.connectionId } : {}),
    };
  }
  config.request = request;
  config.interval = toIsoInterval(Number(form.intervalValue), form.intervalUnit);
  if (form.paginationKind !== "none") {
    const pagination: Record<string, JsonValue> = { kind: form.paginationKind };
    if (form.maxPages.trim()) pagination.maxPages = Number(form.maxPages);
    const pageParameter = form.pageParameter.trim();
    if (form.paginationKind === "page" && pageParameter) {
      pagination.pageParameter = pageParameter;
    }
    if (form.paginationKind === "cursor") {
      pagination.cursorPath = form.cursorPath;
      pagination.cursorParameter = form.cursorParameter.trim();
    }
    config.pagination = pagination;
  }
}

function readHttpSecret(form: SourceFormState): JsonValue | undefined {
  if (form.authKind === "bearer" && form.bearerToken) return form.bearerToken;
  if (form.authKind === "basic" && form.basicUsername && form.basicPassword)
    return { username: form.basicUsername, password: form.basicPassword };
  if (form.authKind === "header" && form.headerValue) return { value: form.headerValue };
  return undefined;
}

export function sourceFormToRequest(
  form: SourceFormState,
  options: SourceFormOptions,
): {
  name: string;
  config: JsonValue;
  secret?: JsonValue;
} {
  const errors = validateSourceForm(form, options);
  if (errors.length > 0) throw new Error(errors[0]);
  const name = getSourceNamePrefix(form.kind, form.managedBy) + form.nameSuffix;
  if (form.kind === EVENT_SOURCE_KIND.provider) {
    return { name, config: form.configurationId ? { configurationId: form.configurationId } : {} };
  }

  const config = buildSourceConfig(form);
  if (form.kind === EVENT_SOURCE_KIND.webhook) {
    addWebhookConfig(form, config);
    return { name, config };
  }
  addHttpConfig(form, config);
  const secret = readHttpSecret(form);
  return secret === undefined ? { name, config } : { name, config, secret };
}

export function sourceFormToCreateRequest(
  form: SourceFormState,
  options: SourceFormOptions,
): EventSourceCreateRequest {
  const fields = sourceFormToRequest(form, options);
  const provider =
    form.kind === EVENT_SOURCE_KIND.provider
      ? { providerExtensionId: form.providerExtensionId.trim(), providerId: form.providerId.trim() }
      : {};
  return { ...fields, ...provider, kind: form.kind, enabled: true };
}
