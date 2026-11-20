import type { Dispatch, ReactNode, SetStateAction } from "react";
import { apiGet, getApiRuntime, type EventSourceResponse } from "@agw/api";
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  Label,
} from "@agw/components";
import { useQuery } from "@agw/components/query";

import { EventEditorFooter, LabeledSelect } from "../form-fields";
import {
  HttpAuthenticationFields,
  HttpLocationFields,
  HttpRequestFields,
  SourceEventTypeFields,
  SourceFormatFields,
  SourceNameField,
  SourceProviderField,
  WebhookSettingsFields,
} from "./source-fields";
import {
  createSourceForm,
  validateSourceForm,
  type CreatedWebhook,
  type SourceFormOptions,
  type SourceFormState,
} from "./source-form";
import { EVENT_SOURCE_KIND } from "./source-name";

type SourceDialogProps = {
  open: boolean;
  editingSource: EventSourceResponse | null;
  form: SourceFormState;
  pending: boolean;
  createdSource: CreatedWebhook | null;
  onFormChange: Dispatch<SetStateAction<SourceFormState>>;
  onOpenChange(open: boolean): void;
  onSubmit(options: SourceFormOptions): void;
};

export function SourceDialog(props: SourceDialogProps) {
  const queries = useSourceDialogQueries(props.open, props.form);
  const errors = validateSourceForm(props.form, queries.options);
  const feedback = queries.paginationError ? "Unable to check pagination settings." : errors[0];
  const providerAvailable =
    props.form.kind !== EVENT_SOURCE_KIND.provider || queries.provider !== undefined;
  const canSave = !props.pending && errors.length === 0 && providerAvailable;
  const title = props.createdSource
    ? "Webhook created"
    : props.editingSource
      ? "Edit source"
      : "Add source";

  return (
    <Dialog open={props.open} onOpenChange={props.onOpenChange}>
      <DialogContent size="2xl" className="max-h-[90vh] overflow-hidden">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>Configure how Agw receives events.</DialogDescription>
        </DialogHeader>
        {props.createdSource ? (
          <WebhookDetails source={props.createdSource} />
        ) : (
          <SourceEditor {...props} queries={queries} feedback={feedback} />
        )}
        <EventEditorFooter
          readOnly={props.createdSource !== null}
          pending={props.pending}
          canSave={canSave}
          saveLabel="Save source"
          onClose={() => props.onOpenChange(false)}
          onSave={() => props.onSubmit(queries.options)}
        />
      </DialogContent>
    </Dialog>
  );
}

function useSourceDialogQueries(open: boolean, form: SourceFormState) {
  const isHttpPoll = form.kind === EVENT_SOURCE_KIND.httpPoll;
  const usesPagination = isHttpPoll && form.paginationKind !== "none";
  const cursorPath =
    form.paginationKind === "cursor" && form.cursorPath.trim() ? form.cursorPath : undefined;
  const providers = useQuery({
    queryKey: ["events", "sources", "providers"],
    queryFn: () => apiGet("/api/events/sources/providers"),
    enabled: open && form.kind === EVENT_SOURCE_KIND.provider,
  });
  const connections = useQuery({
    queryKey: ["integrations", "connections"],
    queryFn: () => apiGet("/api/integrations/connections"),
    enabled: open && isHttpPoll && form.authKind === "connection",
  });
  const pagination = useQuery({
    queryKey: ["events", "sources", "http-poll-options", cursorPath],
    queryFn: ({ signal }) =>
      apiGet("/api/events/sources/http-poll-options", {
        params: { query: { cursorPath } },
        signal,
      }),
    enabled: open && usesPagination,
  });
  const provider = providers.data?.find(
    (item) => item.extensionId === form.providerExtensionId && item.providerId === form.providerId,
  );
  const paginationError = usesPagination && pagination.isError;
  const options: SourceFormOptions = {
    requiresConfiguration: Boolean(provider?.configurations.length),
    httpPoll: paginationError ? undefined : (pagination.data ?? undefined),
  };
  return { provider, providers, connections, options, paginationError };
}

type SourceEditorProps = Pick<SourceDialogProps, "form" | "editingSource" | "onFormChange"> & {
  queries: ReturnType<typeof useSourceDialogQueries>;
  feedback?: string;
};

function SourceEditor({ form, editingSource, onFormChange, queries, feedback }: SourceEditorProps) {
  const update = (changes: Partial<SourceFormState>) =>
    onFormChange((current) => ({ ...current, ...changes }));
  const isProvider = form.kind === EVENT_SOURCE_KIND.provider;
  const fields = { form, update };

  return (
    <div className="-ms-1 max-h-[65vh] space-y-4 overflow-y-auto p-1">
      <SourceKindField
        kind={form.kind}
        editing={editingSource !== null}
        onFormChange={onFormChange}
      />
      {isProvider ? (
        <SourceProviderField
          {...fields}
          providers={queries.providers.data ?? []}
          editing={Boolean(editingSource)}
          unavailable={queries.providers.isError}
        />
      ) : null}
      <SourceNameField {...fields} />
      {isProvider ? (
        <ProviderTypes provider={queries.provider} editingSource={editingSource} />
      ) : (
        <SourceEventTypeFields {...fields} />
      )}
      {form.kind === EVENT_SOURCE_KIND.httpPoll ? (
        <>
          <HttpLocationFields {...fields} />
          <HttpAuthenticationFields {...fields} connections={queries.connections.data ?? []} />
        </>
      ) : null}
      <SourceAdvancedFields {...fields} queries={queries} onFormChange={onFormChange} />
      {feedback ? (
        <p role="alert" className="text-xs text-destructive">
          {feedback}
        </p>
      ) : null}
    </div>
  );
}

function SourceKindField({
  kind,
  editing,
  onFormChange,
}: Pick<SourceDialogProps, "onFormChange"> & {
  kind: number;
  editing: boolean;
}) {
  const changeKind = (value: string) =>
    onFormChange((current) => ({
      ...createSourceForm(Number(value)),
      nameSuffix: current.nameSuffix,
    }));
  return (
    <LabeledSelect
      id="source-kind"
      label="Source type"
      value={kind}
      disabled={editing}
      onValueChange={changeKind}
    >
      <option value={EVENT_SOURCE_KIND.webhook}>Webhook</option>
      <option value={EVENT_SOURCE_KIND.httpPoll}>HTTP poll</option>
      <option value={EVENT_SOURCE_KIND.provider}>Extension provider</option>
    </LabeledSelect>
  );
}

function ProviderTypes({
  provider,
  editingSource,
}: Pick<SourceEditorProps, "editingSource"> & {
  provider: SourceEditorProps["queries"]["provider"];
}) {
  const types = provider?.eventTypes ?? editingSource?.eventTypes;
  return (
    <p className="text-xs text-muted-foreground">
      Declared types: {types?.join(", ") ?? "Select a provider"}
    </p>
  );
}

function SourceAdvancedFields({
  form,
  update,
  queries,
  onFormChange,
}: Omit<SourceEditorProps, "editingSource" | "feedback"> & {
  update(changes: Partial<SourceFormState>): void;
}) {
  const fields = { form, update };
  return (
    <details className="rounded-md border p-3">
      <summary className="cursor-pointer text-sm font-medium">Advanced settings</summary>
      <div className="mt-4 space-y-4">
        {form.kind === EVENT_SOURCE_KIND.provider ? (
          queries.options.requiresConfiguration ? (
            <ProviderConfiguration {...fields} provider={queries.provider} />
          ) : null
        ) : (
          <SourceFormatFields {...fields} />
        )}
        {form.kind === EVENT_SOURCE_KIND.webhook ? <WebhookSettingsFields {...fields} /> : null}
        {form.kind === EVENT_SOURCE_KIND.httpPoll ? (
          <HttpRequestFields
            {...fields}
            onFormChange={onFormChange}
            maxPages={queries.options.httpPoll?.maxPages}
          />
        ) : null}
      </div>
    </details>
  );
}

function ProviderConfiguration({
  form,
  update,
  provider,
}: Pick<SourceEditorProps, "form"> & {
  update(changes: Partial<SourceFormState>): void;
  provider: SourceEditorProps["queries"]["provider"];
}) {
  return (
    <LabeledSelect
      id="source-configuration"
      label="Configuration"
      value={form.configurationId}
      onValueChange={(configurationId) => update({ configurationId })}
    >
      <option value="">Select a configuration</option>
      {provider?.configurations.map(({ id, name, description }) => (
        <option key={id} value={id}>
          {name}
          {description ? ` · ${description}` : ""}
        </option>
      ))}
    </LabeledSelect>
  );
}

function WebhookDetails({ source }: { source: CreatedWebhook }) {
  const address = `${getApiRuntime().baseUrl || window.location.origin}/api/events/webhooks/receive?sourceId=${source.id}`;
  return (
    <div className="space-y-4 overflow-y-auto py-2">
      <CopyWebhookValue
        label="Webhook receive URL"
        value={address}
        testId="webhook-url"
        button="Copy address"
      />
      <CopyWebhookValue
        label="Webhook secret"
        value={source.secret}
        testId="webhook-secret"
        button="Copy secret"
      >
        <p className="text-xs text-muted-foreground">
          This secret is shown only once. Save it now.
        </p>
      </CopyWebhookValue>
    </div>
  );
}

function CopyWebhookValue({
  label,
  value,
  testId,
  button,
  children,
}: {
  label: string;
  value: string;
  testId: string;
  button: string;
  children?: ReactNode;
}) {
  return (
    <div className="space-y-2">
      <Label>{label}</Label>
      <p className="break-all rounded-md border p-2 font-mono text-xs" data-testid={testId}>
        {value}
      </p>
      <Button variant="outline" size="sm" onClick={() => void navigator.clipboard.writeText(value)}>
        {button}
      </Button>
      {children}
    </div>
  );
}
