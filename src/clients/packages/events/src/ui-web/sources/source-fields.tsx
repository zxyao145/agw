import type { Dispatch, SetStateAction } from "react";
import type { EventSourceProviderResponse } from "@agw/api";
import { Button, Input, Label } from "@agw/components";

import { formatCommaSeparatedValues } from "../comma-separated-values";
import { LabeledInput, LabeledSelect, LabeledTextarea } from "../form-fields";
import {
  HTTP_AUTH_OPTIONS,
  HTTP_METHOD_OPTIONS,
  PAGINATION_OPTIONS,
  SOURCE_FORMAT_OPTIONS,
  WEBHOOK_AUTH_OPTIONS,
  type SourceFormState,
} from "./source-form";
import { EVENT_SOURCE_KIND, getSourceNamePrefix } from "./source-name";

type SourceFieldsProps = {
  form: SourceFormState;
  update(changes: Partial<SourceFormState>): void;
};

type Connections = readonly { id: string; displayName: string }[];

function SelectOptions({
  options,
  disabledOption,
}: {
  options: Record<string, string>;
  disabledOption?: string;
}) {
  return Object.entries(options).map(([value, label]) => (
    <option key={value} value={value} disabled={value === disabledOption}>
      {label}
    </option>
  ));
}

function sourceProviderValue(extensionId: string, providerId: string): string {
  return `${extensionId}:${providerId}`;
}

export function SourceProviderField({
  form,
  update,
  providers,
  editing,
  unavailable,
}: SourceFieldsProps & {
  providers: readonly EventSourceProviderResponse[];
  editing: boolean;
  unavailable: boolean;
}) {
  const selectProvider = (value: string) => {
    const provider = providers.find(
      (item) => sourceProviderValue(item.extensionId, item.providerId) === value,
    );
    update(
      provider
        ? {
            providerExtensionId: provider.extensionId,
            providerId: provider.providerId,
            nameSuffix: provider.extensionName,
            configurationId: "",
          }
        : { providerExtensionId: "", providerId: "", configurationId: "" },
    );
  };
  return (
    <div className="space-y-2">
      <LabeledSelect
        id="source-provider"
        label="Source provider"
        disabled={editing}
        value={sourceProviderValue(form.providerExtensionId, form.providerId)}
        onValueChange={selectProvider}
      >
        <option value=":">Select a source provider</option>
        {providers.map((provider) => {
          const value = sourceProviderValue(provider.extensionId, provider.providerId);
          return (
            <option key={value} value={value}>
              {provider.extensionDisplayName} · {provider.providerId}
            </option>
          );
        })}
      </LabeledSelect>
      {unavailable ? (
        <p role="alert" className="text-xs text-destructive">
          Unable to load source providers.
        </p>
      ) : null}
    </div>
  );
}

export function SourceNameField({ form, update }: SourceFieldsProps) {
  return (
    <div className="space-y-2">
      <Label htmlFor="source-name">Name</Label>
      <div className="flex items-center rounded-md border">
        <span className="shrink-0 px-3 font-mono text-sm text-muted-foreground">
          {getSourceNamePrefix(form.kind, form.managedBy)}
        </span>
        <Input
          id="source-name"
          className="border-0"
          value={form.nameSuffix}
          onChange={(event) => update({ nameSuffix: event.currentTarget.value })}
        />
      </div>
    </div>
  );
}

const TYPE_MODES = [
  ["declared", "Use event type"],
  ["fixed", "Fixed type"],
] as const;

export function SourceEventTypeFields({ form, update }: SourceFieldsProps) {
  return (
    <>
      <SourceTypeModeField form={form} update={update} />
      {form.typeMode === "fixed" ? (
        <LabeledInput
          id="source-fixed-type"
          label="Fixed event type"
          value={form.fixedType}
          onValueChange={(fixedType) => update({ fixedType })}
        />
      ) : (
        <LabeledInput
          id="source-event-types"
          label="Event types"
          value={formatCommaSeparatedValues(form.eventTypes)}
          onValueChange={(eventTypes) => update({ eventTypes })}
          placeholder="demo.ticket.created, demo.order.*"
          description={
            <p className="text-xs text-muted-foreground">Separate multiple types with commas.</p>
          }
        />
      )}
    </>
  );
}

function SourceTypeModeField({ form, update }: SourceFieldsProps) {
  return (
    <fieldset className="space-y-2">
      <legend className="text-sm font-medium">Event type</legend>
      {TYPE_MODES.map(([typeMode, label]) => (
        <label
          key={typeMode}
          className={
            typeMode === "declared"
              ? "mr-4 inline-flex items-center gap-2 text-sm"
              : "inline-flex items-center gap-2 text-sm"
          }
        >
          <input
            type="radio"
            name="source-type-mode"
            checked={form.typeMode === typeMode}
            onChange={() => update({ typeMode })}
          />
          {label}
        </label>
      ))}
    </fieldset>
  );
}

export function HttpLocationFields({ form, update }: SourceFieldsProps) {
  return (
    <>
      <LabeledInput
        id="source-url"
        label="URL"
        type="url"
        value={form.url}
        onValueChange={(url) => update({ url })}
      />
      <PollIntervalField form={form} update={update} />
    </>
  );
}

function PollIntervalField({ form, update }: SourceFieldsProps) {
  return (
    <div className="space-y-2">
      <Label htmlFor="source-interval">Poll interval</Label>
      <div className="flex gap-2">
        <Input
          id="source-interval"
          type="number"
          min={1}
          value={form.intervalValue}
          onChange={(event) => update({ intervalValue: event.currentTarget.value })}
        />
        <select
          aria-label="Poll interval unit"
          className="rounded-md border bg-background px-2 text-sm"
          value={form.intervalUnit}
          onChange={(event) =>
            update({ intervalUnit: event.currentTarget.value as SourceFormState["intervalUnit"] })
          }
        >
          <option value="minutes">Minutes</option>
          <option value="hours">Hours</option>
          <option value="days">Days</option>
        </select>
      </div>
      {form.originalInterval && !form.intervalValue ? (
        <p className="text-xs text-destructive">
          Current interval: {form.originalInterval}. Choose a new interval before saving.
        </p>
      ) : null}
    </div>
  );
}

export function HttpAuthenticationFields({
  form,
  update,
  connections,
}: SourceFieldsProps & { connections: Connections }) {
  return (
    <>
      <LabeledSelect
        id="source-auth"
        label="Authentication"
        value={form.authKind}
        onValueChange={(value) => update({ authKind: value as SourceFormState["authKind"] })}
      >
        <SelectOptions options={HTTP_AUTH_OPTIONS} />
      </LabeledSelect>
      <HttpCredentials form={form} update={update} connections={connections} />
    </>
  );
}

function HttpCredentials({
  form,
  update,
  connections,
}: SourceFieldsProps & { connections: Connections }) {
  switch (form.authKind) {
    case "bearer":
      return (
        <LabeledInput
          id="source-bearer-token"
          label="Bearer Token"
          type="password"
          value={form.bearerToken}
          placeholder={
            form.savedSecretKind === "bearer" ? "Leave blank to keep the current token" : undefined
          }
          onValueChange={(bearerToken) => update({ bearerToken })}
        />
      );
    case "basic":
      return <HttpBasicCredentials form={form} update={update} />;
    case "header":
      return <HttpHeaderCredentials form={form} update={update} />;
    case "connection":
      return (
        <LabeledSelect
          id="source-connection"
          label="Connection"
          value={form.connectionId}
          onValueChange={(connectionId) => update({ connectionId })}
        >
          <option value="">Select a connection</option>
          {connections.map(({ id, displayName }) => (
            <option key={id} value={id}>
              {displayName}
            </option>
          ))}
        </LabeledSelect>
      );
    case "none":
      return null;
  }
}

function HttpBasicCredentials({ form, update }: SourceFieldsProps) {
  return (
    <div className="grid gap-3 sm:grid-cols-2">
      <LabeledInput
        id="source-basic-username"
        label="Username"
        value={form.basicUsername}
        onValueChange={(basicUsername) => update({ basicUsername })}
      />
      <LabeledInput
        id="source-basic-password"
        label="Password"
        type="password"
        value={form.basicPassword}
        onValueChange={(basicPassword) => update({ basicPassword })}
      />
    </div>
  );
}

function HttpHeaderCredentials({ form, update }: SourceFieldsProps) {
  return (
    <div className="grid gap-3 sm:grid-cols-2">
      <LabeledInput
        id="source-header-name"
        label="Header name"
        value={form.headerName}
        onValueChange={(headerName) => update({ headerName })}
      />
      <LabeledInput
        id="source-header-value"
        label="Header value"
        type="password"
        value={form.headerValue}
        onValueChange={(headerValue) => update({ headerValue })}
      />
    </div>
  );
}

export function SourceFormatFields({ form, update }: SourceFieldsProps) {
  const webhook = form.kind === EVENT_SOURCE_KIND.webhook;
  const labels = webhook
    ? { ...SOURCE_FORMAT_OPTIONS, mapped: "Custom mapping" }
    : SOURCE_FORMAT_OPTIONS;
  const changeFormat = (format: SourceFormState["format"]) => {
    const resetCursor = format === "cloudevents" && form.paginationKind === "cursor";
    update(resetCursor ? { format, paginationKind: "none" } : { format });
  };
  return (
    <>
      <LabeledSelect
        id="source-format"
        label={webhook ? "Request format" : "Response format"}
        value={form.format}
        onValueChange={(value) => changeFormat(value as SourceFormState["format"])}
      >
        <SelectOptions options={labels} />
      </LabeledSelect>
      {form.format === "mapped" ? (
        <LabeledTextarea
          id="source-mapping"
          label="Mapping JSON"
          monospace
          rows={6}
          value={form.mappingText}
          onValueChange={(mappingText) => update({ mappingText })}
        />
      ) : webhook ? (
        <LabeledTextarea
          id="source-cloud-event"
          label="CloudEvent display mapping JSON"
          monospace
          rows={4}
          value={form.cloudEventText}
          onValueChange={(cloudEventText) => update({ cloudEventText })}
        />
      ) : null}
    </>
  );
}

export function WebhookSettingsFields({ form, update }: SourceFieldsProps) {
  return (
    <>
      <LabeledSelect
        id="source-webhook-auth"
        label="Webhook authentication"
        value={form.webhookAuthKind}
        onValueChange={(value) =>
          update({ webhookAuthKind: value as SourceFormState["webhookAuthKind"] })
        }
      >
        <SelectOptions options={WEBHOOK_AUTH_OPTIONS} />
      </LabeledSelect>
      {form.webhookAuthKind === "BearerToken" ? (
        <LabeledInput
          id="source-token-header"
          label="Token header"
          value={form.tokenHeader}
          onValueChange={(tokenHeader) => update({ tokenHeader })}
        />
      ) : (
        <WebhookHmacFields form={form} update={update} />
      )}
      <LabeledInput
        id="source-content-types"
        label="Content-Type allowlist"
        value={formatCommaSeparatedValues(form.contentTypes)}
        onValueChange={(contentTypes) => update({ contentTypes })}
        placeholder="application/cloudevents+json, application/json"
      />
    </>
  );
}

function WebhookHmacFields({ form, update }: SourceFieldsProps) {
  return (
    <>
      <LabeledInput
        id="source-signature-header"
        label="Signature header"
        value={form.signatureHeader}
        onValueChange={(signatureHeader) => update({ signatureHeader })}
      />
      <LabeledInput
        id="source-timestamp-header"
        label="Timestamp header"
        value={form.timestampHeader}
        onValueChange={(timestampHeader) => update({ timestampHeader })}
      />
      <LabeledInput
        id="source-clock-skew"
        label="Max clock skew in seconds"
        type="number"
        min={0}
        max={3600}
        step={1}
        value={form.maxClockSkewSeconds}
        onValueChange={(maxClockSkewSeconds) => update({ maxClockSkewSeconds })}
      />
    </>
  );
}

type HttpHeaderFieldsProps = Pick<SourceFieldsProps, "form"> & {
  onFormChange: Dispatch<SetStateAction<SourceFormState>>;
};

export function HttpRequestFields({
  form,
  update,
  onFormChange,
  maxPages,
}: SourceFieldsProps & HttpHeaderFieldsProps & { maxPages?: number }) {
  return (
    <>
      <LabeledSelect
        id="source-method"
        label="Request method"
        value={form.method}
        onValueChange={(value) => update({ method: value as SourceFormState["method"] })}
      >
        <SelectOptions options={HTTP_METHOD_OPTIONS} />
      </LabeledSelect>
      <HttpHeaderFields form={form} onFormChange={onFormChange} />
      {form.method === "POST" ? (
        <LabeledTextarea
          id="source-body"
          label="Request body JSON"
          monospace
          rows={4}
          value={form.bodyText}
          onValueChange={(bodyText) => update({ bodyText })}
        />
      ) : null}
      <HttpPaginationFields form={form} update={update} maxPages={maxPages} />
    </>
  );
}

type RequestHeader = SourceFormState["headers"][number];

function HttpHeaderFields({ form, onFormChange }: HttpHeaderFieldsProps) {
  const update = (index: number, change: Partial<RequestHeader>) =>
    onFormChange((current) => ({
      ...current,
      headers: current.headers.map((header, rowIndex) =>
        rowIndex === index ? { ...header, ...change } : header,
      ),
    }));
  const remove = (index: number) =>
    onFormChange((current) => ({
      ...current,
      headers: current.headers.filter((_, rowIndex) => rowIndex !== index),
    }));
  const add = () =>
    onFormChange((current) => ({
      ...current,
      headers: [...current.headers, { name: "", value: "" }],
    }));
  return (
    <div className="space-y-2">
      <Label>Request headers</Label>
      {form.headers.map((header, index) => (
        <HttpHeaderRow
          key={index}
          header={header}
          number={index + 1}
          update={(change) => update(index, change)}
          remove={() => remove(index)}
        />
      ))}
      <Button variant="outline" size="sm" onClick={add}>
        Add header
      </Button>
    </div>
  );
}

function HttpHeaderRow({
  header,
  number,
  update,
  remove,
}: {
  header: RequestHeader;
  number: number;
  update(change: Partial<RequestHeader>): void;
  remove(): void;
}) {
  return (
    <div className="flex gap-2">
      <Input
        aria-label={`Header ${number} name`}
        value={header.name}
        placeholder="Header name"
        onChange={(event) => update({ name: event.currentTarget.value })}
      />
      <Input
        aria-label={`Header ${number} value`}
        value={header.value}
        placeholder="Header value"
        onChange={(event) => update({ value: event.currentTarget.value })}
      />
      <Button variant="outline" size="sm" aria-label={`Remove header ${number}`} onClick={remove}>
        Remove
      </Button>
    </div>
  );
}

function HttpPaginationFields({
  form,
  update,
  maxPages,
}: SourceFieldsProps & { maxPages?: number }) {
  return (
    <>
      <LabeledSelect
        id="source-pagination"
        label="Pagination"
        value={form.paginationKind}
        onValueChange={(value) =>
          update({ paginationKind: value as SourceFormState["paginationKind"] })
        }
      >
        <SelectOptions
          options={PAGINATION_OPTIONS}
          disabledOption={form.format === "cloudevents" ? "cursor" : undefined}
        />
      </LabeledSelect>
      {form.paginationKind === "none" ? null : (
        <LabeledInput
          id="source-max-pages"
          label="Maximum pages"
          type="number"
          min={1}
          max={maxPages}
          step={1}
          value={form.maxPages}
          onValueChange={(maximum) => update({ maxPages: maximum })}
        />
      )}
      {form.paginationKind === "page" ? (
        <LabeledInput
          id="source-page-parameter"
          label="Page parameter"
          value={form.pageParameter}
          onValueChange={(pageParameter) => update({ pageParameter })}
        />
      ) : null}
      {form.paginationKind === "cursor" ? <HttpCursorFields form={form} update={update} /> : null}
    </>
  );
}

function HttpCursorFields({ form, update }: SourceFieldsProps) {
  return (
    <>
      <LabeledInput
        id="source-cursor-path"
        label="Cursor path"
        value={form.cursorPath}
        onValueChange={(cursorPath) => update({ cursorPath })}
      />
      <LabeledInput
        id="source-cursor-parameter"
        label="Cursor parameter"
        value={form.cursorParameter}
        onValueChange={(cursorParameter) => update({ cursorParameter })}
      />
    </>
  );
}
