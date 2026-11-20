import * as React from "react";
import type { EventSourceOptionResponse } from "@agw/api";
import {
  Button,
  Input,
  Label,
  SearchableSelect,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@agw/components";

import { LabeledInput, LabeledTextarea } from "../form-fields";
import { isAllowedEventType } from "./event-type-options";
import {
  EXECUTION_PERMISSION_MODES,
  getHandlerConfigurationError,
  SUBSCRIPTION_TARGET_KINDS,
  type SubscriptionFormState,
} from "./subscription-form";

export type SubscriptionFieldsProps = {
  form: SubscriptionFormState;
  readOnly: boolean;
  update(changes: Partial<SubscriptionFormState>): void;
};

export function SubscriptionNameField({ form, readOnly, update }: SubscriptionFieldsProps) {
  return (
    <div className="md:col-span-2">
      <LabeledInput
        id="subscription-name"
        label="Name"
        value={form.name}
        disabled={readOnly}
        onValueChange={(name) => update({ name })}
      />
    </div>
  );
}

export function SubscriptionInstructionsField({ form, readOnly, update }: SubscriptionFieldsProps) {
  return (
    <div className="md:col-span-2">
      <LabeledTextarea
        id="subscription-instructions"
        label="Instructions"
        rows={3}
        value={form.instructions}
        disabled={readOnly}
        onValueChange={(instructions) => update({ instructions })}
      />
    </div>
  );
}

type EventFieldsProps = SubscriptionFieldsProps & {
  open: boolean;
  sources: readonly EventSourceOptionResponse[];
  sourceLoading: boolean;
  sourceError: boolean;
  types: readonly string[];
  invalidTypes: readonly string[];
};

export function SubscriptionEventFields(props: EventFieldsProps) {
  return (
    <>
      <SubscriptionSources {...props} />
      <SubscriptionEventTypes {...props} />
    </>
  );
}

function SubscriptionSources({
  form,
  readOnly,
  update,
  sources,
  sourceLoading,
  sourceError,
}: EventFieldsProps) {
  return (
    <div className="space-y-2 md:col-span-2">
      {readOnly ? (
        <dl className="space-y-2">
          <dt className="text-sm font-medium">Sources</dt>
          <dd className="wrap-anywhere text-sm">
            {form.sourceIds
              .map((id) => sources.find((source) => source.id === id)?.name ?? id)
              .join(", ") || "No sources selected"}
          </dd>
        </dl>
      ) : (
        <SearchableSelect
          id="subscription-sources"
          label="Sources"
          multiple
          placeholder="Select sources"
          searchPlaceholder="Search sources"
          isLoading={sourceLoading}
          options={sources.map((source) => ({ value: source.id, title: source.name }))}
          value={form.sourceIds}
          onValueChange={(sourceIds) => update({ sourceIds })}
        />
      )}
      {sourceError ? (
        <p role="alert" className="text-xs text-destructive">
          Unable to load sources.
        </p>
      ) : null}
    </div>
  );
}

function SubscriptionEventTypes({
  form,
  readOnly,
  update,
  open,
  types,
  invalidTypes,
}: EventFieldsProps) {
  const [draft, setDraft] = React.useState("");
  React.useEffect(() => {
    if (!open) setDraft("");
  }, [open]);
  const type = draft.trim();
  const allowed = isAllowedEventType(type, types);
  const add = () => {
    if (allowed && !form.eventTypes.includes(type)) {
      update({ eventTypes: [...form.eventTypes, type] });
      setDraft("");
    }
  };
  return (
    <div className="space-y-2 md:col-span-2">
      <Label htmlFor="subscription-event-type-input">Event types</Label>
      <EventTypeInput
        draft={draft}
        setDraft={setDraft}
        types={types}
        add={add}
        readOnly={readOnly}
        allowed={allowed}
        hasSources={form.sourceIds.length > 0}
      />
      <SelectedEventTypes {...{ form, readOnly, update }} invalidTypes={invalidTypes} />
    </div>
  );
}

function EventTypeInput({
  draft,
  setDraft,
  types,
  add,
  readOnly,
  allowed,
  hasSources,
}: {
  draft: string;
  setDraft(value: string): void;
  types: readonly string[];
  add(): void;
  readOnly: boolean;
  allowed: boolean;
  hasSources: boolean;
}) {
  return (
    <>
      <div className="flex gap-2">
        <Input
          id="subscription-event-type-input"
          list="subscription-event-type-options"
          value={draft}
          disabled={readOnly || !hasSources}
          onChange={(event) => setDraft(event.currentTarget.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault();
              add();
            }
          }}
          placeholder="Choose or enter a complete type"
        />
        <Button type="button" variant="outline" onClick={add} disabled={readOnly || !allowed}>
          Add type
        </Button>
      </div>
      <datalist id="subscription-event-type-options">
        {types.map((type) => (
          <option key={type} value={type} />
        ))}
      </datalist>
      {draft && !allowed ? (
        <p role="alert" className="text-xs text-destructive">
          Type must match a selected source declaration.
        </p>
      ) : null}
    </>
  );
}

function SelectedEventTypes({
  form,
  readOnly,
  update,
  invalidTypes,
}: SubscriptionFieldsProps & {
  invalidTypes: readonly string[];
}) {
  return (
    <>
      <div className="flex flex-wrap gap-2">
        {form.eventTypes.map((type) => (
          <span
            key={type}
            className="inline-flex items-center gap-2 rounded-md border px-2 py-1 text-xs"
          >
            {type}
            {readOnly ? null : (
              <button
                type="button"
                aria-label={`Remove ${type}`}
                onClick={() =>
                  update({ eventTypes: form.eventTypes.filter((item) => item !== type) })
                }
              >
                ×
              </button>
            )}
          </span>
        ))}
      </div>
      {invalidTypes.length > 0 ? (
        <p role="alert" className="text-xs text-destructive">
          Selected type is not declared by a selected source: {invalidTypes.join(", ")}
        </p>
      ) : null}
    </>
  );
}

const NO_PROJECT = "none";

type Choice = { id: string; name: string; displayName?: string | null };
type ChoiceQuery = { data?: readonly Choice[] | null; isPending: boolean; isError: boolean };
type TargetFieldsProps = SubscriptionFieldsProps & {
  pending: boolean;
  targetLabel: string;
  targets: ChoiceQuery;
  projects: ChoiceQuery;
};

const TARGET_TYPES = [
  { id: String(SUBSCRIPTION_TARGET_KINDS.agent), name: "Agent" },
  { id: String(SUBSCRIPTION_TARGET_KINDS.agentflow), name: "Agentflow" },
  { id: String(SUBSCRIPTION_TARGET_KINDS.extensionHandler), name: "Extension handler" },
];

export function SubscriptionTargetFields(props: TargetFieldsProps) {
  const { form, update, readOnly } = props;
  const targetsExtension = form.targetKind === SUBSCRIPTION_TARGET_KINDS.extensionHandler;
  return (
    <>
      <SubscriptionChoice
        id="subscription-target"
        label="Target type"
        value={String(form.targetKind)}
        disabled={readOnly}
        options={TARGET_TYPES}
        onValueChange={(value) => update({ targetKind: Number(value), agentId: "" })}
      />
      {targetsExtension ? (
        <SubscriptionHandlerFields {...props} />
      ) : (
        <SubscriptionAgentField {...props} />
      )}
      <SubscriptionProjectField {...props} targetsExtension={targetsExtension} />
      {targetsExtension ? null : <SubscriptionPermissionField {...props} />}
    </>
  );
}

function SubscriptionHandlerFields({ form, readOnly, update }: SubscriptionFieldsProps) {
  const error = getHandlerConfigurationError(form.handlerConfigText);
  return (
    <>
      <LabeledInput
        id="subscription-handler-extension"
        label="Handler extension ID"
        value={form.handlerExtensionId}
        disabled={readOnly}
        onValueChange={(handlerExtensionId) => update({ handlerExtensionId })}
      />
      <LabeledInput
        id="subscription-handler-id"
        label="Handler ID"
        value={form.handlerId}
        disabled={readOnly}
        onValueChange={(handlerId) => update({ handlerId })}
      />
      <div className="space-y-2 md:col-span-2">
        <LabeledTextarea
          id="subscription-handler-config"
          label="Handler configuration JSON"
          monospace
          rows={4}
          value={form.handlerConfigText}
          disabled={readOnly}
          onValueChange={(handlerConfigText) => update({ handlerConfigText })}
        />
        {error ? (
          <p role="alert" className="text-xs text-destructive">
            {error}
          </p>
        ) : null}
      </div>
    </>
  );
}

function choicePlaceholder(query: ChoiceQuery, pluralLabel: string, selection: string): string {
  if (query.isPending) return `Loading ${pluralLabel}…`;
  if (query.isError) return `Unable to load ${pluralLabel}`;
  return query.data?.length ? selection : `No ${pluralLabel} available`;
}

function SubscriptionAgentField({
  form,
  readOnly,
  update,
  pending,
  targetLabel,
  targets,
}: TargetFieldsProps) {
  const options = targets.data ?? [];
  const selected = options.some((target) => target.id === form.agentId) ? form.agentId : "";
  return (
    <SubscriptionChoice
      id="subscription-agent"
      label={targetLabel}
      value={selected}
      required
      disabled={pending || readOnly || options.length === 0}
      options={options}
      placeholder={choicePlaceholder(targets, `${targetLabel}s`, `Select an ${targetLabel}`)}
      onValueChange={(agentId) => update({ agentId })}
    />
  );
}

function SubscriptionProjectField({
  form,
  pending,
  readOnly,
  update,
  projects,
  targetsExtension,
}: TargetFieldsProps & {
  targetsExtension: boolean;
}) {
  const options = projects.data ?? [];
  const hasProject = options.some((project) => project.id === form.projectId);
  const selected =
    targetsExtension && !form.projectId ? NO_PROJECT : hasProject ? form.projectId : "";
  const projectOptions = targetsExtension
    ? [{ id: NO_PROJECT, name: "No project" }, ...options]
    : options;
  return (
    <SubscriptionChoice
      id="subscription-project"
      label="Project"
      value={selected}
      required={!targetsExtension}
      disabled={pending || readOnly || projectOptions.length === 0}
      options={projectOptions}
      placeholder={choicePlaceholder(projects, "projects", "Select a project")}
      onValueChange={(value) => update({ projectId: value === NO_PROJECT ? "" : value })}
    />
  );
}

const PERMISSIONS = [
  { id: String(EXECUTION_PERMISSION_MODES.fullAccess), name: "Full access" },
  { id: String(EXECUTION_PERMISSION_MODES.alwaysAsk), name: "Always ask" },
  { id: String(EXECUTION_PERMISSION_MODES.allowSameArguments), name: "Allow same arguments" },
];

function SubscriptionPermissionField({ form, readOnly, update, pending }: TargetFieldsProps) {
  return (
    <SubscriptionChoice
      id="subscription-permission"
      label="Execution permission"
      value={String(form.permissionMode)}
      disabled={pending || readOnly}
      options={PERMISSIONS}
      onValueChange={(value) => update({ permissionMode: Number(value) })}
    />
  );
}

function SubscriptionChoice({
  id,
  label,
  options,
  placeholder,
  ...props
}: {
  id: string;
  label: string;
  options: readonly Choice[];
  placeholder?: string;
  value: string;
  required?: boolean;
  disabled: boolean;
  onValueChange(value: string): void;
}) {
  return (
    <div className="space-y-2">
      <Label htmlFor={id}>{label}</Label>
      <Select {...props}>
        <SelectTrigger id={id} className="w-full">
          <SelectValue placeholder={placeholder} />
        </SelectTrigger>
        <SelectContent position="popper" align="start">
          {options.map((option) => (
            <SelectItem key={option.id} value={option.id}>
              {option.displayName?.trim() || option.name}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}
