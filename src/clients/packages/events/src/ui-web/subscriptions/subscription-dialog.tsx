import type { Dispatch, SetStateAction } from "react";
import { apiGet, type EventSubscriptionResponse } from "@agw/api";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@agw/components";
import { useQuery } from "@agw/components/query";

import { EventEditorFooter } from "../form-fields";
import { isUserManaged } from "../managed-record";
import { getEventTypeOptions, isAllowedEventType } from "./event-type-options";
import {
  SubscriptionEventFields,
  SubscriptionInstructionsField,
  SubscriptionNameField,
  SubscriptionTargetFields,
} from "./subscription-fields";
import { SubscriptionFilterFields } from "./subscription-filter-fields";
import { validateFilterRows } from "./subscription-filter";
import {
  getSubscriptionFormError,
  SUBSCRIPTION_TARGET_KINDS,
  type SubscriptionFormState,
} from "./subscription-form";

type SubscriptionDialogProps = {
  open: boolean;
  editingSubscription: EventSubscriptionResponse | null;
  form: SubscriptionFormState;
  pending: boolean;
  onFormChange: Dispatch<SetStateAction<SubscriptionFormState>>;
  onOpenChange(open: boolean): void;
  onSubmit(): void;
};

export function SubscriptionDialog(props: SubscriptionDialogProps) {
  const readOnly =
    props.editingSubscription !== null && !isUserManaged(props.editingSubscription.managedBy);
  const choices = useSubscriptionChoices(props.open, props.form);
  const filterErrors = validateFilterRows(props.form.filterRows);
  const canSave =
    !props.pending &&
    !readOnly &&
    filterErrors.length === 0 &&
    canSaveSubscription(props.form, choices);
  const title = readOnly
    ? "View subscription"
    : props.editingSubscription
      ? "Edit subscription"
      : "Add subscription";

  return (
    <Dialog open={props.open} onOpenChange={props.onOpenChange}>
      <DialogContent
        size="2xl"
        className="max-h-[90vh] overflow-hidden"
        onEscapeKeyDown={(event) => {
          // 由来源下拉框处理 Escape。Let the source dropdown handle Escape.
          if (
            event.target instanceof Element &&
            event.target.closest('[data-slot="combobox-content"][data-open]')
          ) {
            event.preventDefault();
          }
        }}
      >
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>
            Choose sources, event types, and a target for matching events.
          </DialogDescription>
        </DialogHeader>
        <SubscriptionEditor
          {...props}
          readOnly={readOnly}
          choices={choices}
          filterErrors={filterErrors}
        />
        <EventEditorFooter
          readOnly={readOnly}
          pending={props.pending}
          canSave={canSave}
          saveLabel="Save subscription"
          onClose={() => props.onOpenChange(false)}
          onSave={props.onSubmit}
        />
      </DialogContent>
    </Dialog>
  );
}

function useSubscriptionChoices(open: boolean, form: SubscriptionFormState) {
  const isAgentflow = form.targetKind === SUBSCRIPTION_TARGET_KINDS.agentflow;
  const sources = useQuery({
    queryKey: ["events", "sources", "options"],
    queryFn: () => apiGet("/api/events/sources/options"),
    enabled: open,
  });
  const agents = useQuery({
    queryKey: ["agents"],
    queryFn: () => apiGet("/api/agents"),
    enabled: open && form.targetKind === SUBSCRIPTION_TARGET_KINDS.agent,
  });
  const agentflows = useQuery({
    queryKey: ["agentflows"],
    queryFn: () => apiGet("/api/agentflows"),
    enabled: open && isAgentflow,
  });
  const projects = useQuery({
    queryKey: ["projects"],
    queryFn: () => apiGet("/api/projects"),
    enabled: open,
  });
  const types = getEventTypeOptions(sources.data ?? [], form.sourceIds);
  const invalidTypes = form.eventTypes.filter((type) => !isAllowedEventType(type, types));
  const targets = isAgentflow ? agentflows : agents;
  return {
    sources,
    types,
    invalidTypes,
    targets,
    projects,
    targetLabel: isAgentflow ? "Agentflow" : "Agent",
  };
}

type SubscriptionChoices = ReturnType<typeof useSubscriptionChoices>;

function canSaveSubscription(form: SubscriptionFormState, choices: SubscriptionChoices): boolean {
  const targetsExtension = form.targetKind === SUBSCRIPTION_TARGET_KINDS.extensionHandler;
  const hasTarget = choices.targets.data?.some((target) => target.id === form.agentId);
  const hasProject = choices.projects.data?.some((project) => project.id === form.projectId);
  const allowsEmptyProject = targetsExtension && !form.projectId;
  return (
    getSubscriptionFormError(form) === null &&
    choices.invalidTypes.length === 0 &&
    (targetsExtension || hasTarget === true) &&
    (allowsEmptyProject || hasProject === true)
  );
}

type SubscriptionEditorProps = Pick<
  SubscriptionDialogProps,
  "form" | "pending" | "editingSubscription" | "onFormChange" | "open"
> & {
  readOnly: boolean;
  choices: SubscriptionChoices;
  filterErrors: string[];
};

function SubscriptionEditor({
  form,
  pending,
  editingSubscription,
  onFormChange,
  open,
  readOnly,
  choices,
  filterErrors,
}: SubscriptionEditorProps) {
  const update = (changes: Partial<SubscriptionFormState>) =>
    onFormChange((current) => ({ ...current, ...changes }));
  const fields = { form, readOnly, update };
  return (
    <div className="-ms-1 grid max-h-[65vh] gap-4 overflow-y-auto p-1 md:grid-cols-2">
      <SubscriptionNameField {...fields} />
      <SubscriptionEventFields
        {...fields}
        open={open}
        sources={choices.sources.data ?? []}
        sourceLoading={choices.sources.isPending}
        sourceError={choices.sources.isError}
        types={choices.types}
        invalidTypes={choices.invalidTypes}
      />
      <SubscriptionTargetFields
        {...fields}
        pending={pending}
        targetLabel={choices.targetLabel}
        targets={choices.targets}
        projects={choices.projects}
      />
      <SubscriptionInstructionsField {...fields} />
      <SubscriptionFilterFields
        {...fields}
        existingFilter={editingSubscription?.filter}
        errors={filterErrors}
      />
    </div>
  );
}
