import type { EventSubscriptionRequest, EventSubscriptionResponse, JsonValue } from "@agw/api";

import { tryParseJson } from "../json-values";
import {
  filterRowsToJsonLogic,
  jsonLogicToFilterRows,
  type FilterRow,
} from "./subscription-filter";

export const EXECUTION_PERMISSION_MODES = {
  fullAccess: 0,
  alwaysAsk: 1,
  allowSameArguments: 2,
} as const;
export const SUBSCRIPTION_TARGET_KINDS = { agent: 0, agentflow: 1, extensionHandler: 2 } as const;

export type SubscriptionFormState = {
  name: string;
  enabled: boolean;
  sourceIds: string[];
  eventTypes: string[];
  targetKind: number;
  agentId: string;
  projectId: string;
  permissionMode: number;
  instructions: string;
  filterRows: FilterRow[];
  unsupportedFilter: JsonValue | null;
  handlerExtensionId: string;
  handlerId: string;
  handlerConfigText: string;
};

export function createSubscriptionForm(): SubscriptionFormState {
  return {
    name: "",
    enabled: true,
    sourceIds: [],
    eventTypes: [],
    targetKind: SUBSCRIPTION_TARGET_KINDS.agent,
    agentId: "",
    projectId: "",
    permissionMode: EXECUTION_PERMISSION_MODES.fullAccess,
    instructions: "",
    filterRows: [],
    unsupportedFilter: null,
    handlerExtensionId: "",
    handlerId: "",
    handlerConfigText: "",
  };
}

export function subscriptionToForm(subscription: EventSubscriptionResponse): SubscriptionFormState {
  const rows = jsonLogicToFilterRows(subscription.filter);
  return {
    ...createSubscriptionForm(),
    name: subscription.name,
    enabled: subscription.enabled,
    sourceIds: [...subscription.sourceIds],
    eventTypes: [...subscription.eventTypes],
    targetKind: subscription.targetKind,
    agentId: subscription.agentId ?? "",
    projectId: subscription.projectId ?? "",
    permissionMode: subscription.permissionMode ?? EXECUTION_PERMISSION_MODES.alwaysAsk,
    instructions: subscription.instructions ?? "",
    filterRows: rows ?? [],
    unsupportedFilter: rows === null ? (subscription.filter ?? null) : null,
    handlerExtensionId: subscription.handlerExtensionId ?? "",
    handlerId: subscription.handlerId ?? "",
    handlerConfigText: subscription.handlerConfig
      ? JSON.stringify(subscription.handlerConfig, null, 2)
      : "",
  };
}

export function getHandlerConfigurationError(text: string): string | null {
  return text.trim() && tryParseJson(text) === undefined
    ? "Handler configuration must be valid JSON."
    : null;
}

export function getSubscriptionFormError(form: SubscriptionFormState): string | null {
  const hasRequiredFields =
    Boolean(form.name.trim()) && form.sourceIds.length > 0 && form.eventTypes.length > 0;
  if (!hasRequiredFields)
    return "Name, at least one source, and at least one event type are required.";

  if (form.targetKind === SUBSCRIPTION_TARGET_KINDS.extensionHandler) {
    const hasHandler = Boolean(form.handlerExtensionId.trim() && form.handlerId.trim());
    return hasHandler
      ? getHandlerConfigurationError(form.handlerConfigText)
      : "Extension ID and handler ID are required for an extension handler target.";
  }

  return form.agentId && form.projectId ? null : "Select an Agent or Agentflow and a project.";
}

export function subscriptionFormToRequest(
  form: SubscriptionFormState,
  editingSubscription: EventSubscriptionResponse | null,
): EventSubscriptionRequest {
  const formError = getSubscriptionFormError(form);
  if (formError) throw new Error(formError);

  const isExtension = form.targetKind === SUBSCRIPTION_TARGET_KINDS.extensionHandler;
  const handlerConfig =
    isExtension && form.handlerConfigText.trim()
      ? (JSON.parse(form.handlerConfigText) as JsonValue)
      : undefined;
  return {
    ...(editingSubscription
      ? { id: editingSubscription.id, version: editingSubscription.version }
      : {}),
    name: form.name.trim(),
    enabled: form.enabled,
    sourceIds: form.sourceIds,
    eventTypes: form.eventTypes,
    filter: form.unsupportedFilter ?? filterRowsToJsonLogic(form.filterRows),
    targetKind: form.targetKind,
    agentId: isExtension ? null : form.agentId,
    projectId: form.projectId || null,
    permissionMode: isExtension ? null : form.permissionMode,
    instructions: form.instructions.trim() || null,
    handlerExtensionId: isExtension ? form.handlerExtensionId.trim() : null,
    handlerId: isExtension ? form.handlerId.trim() : null,
    ...(handlerConfig === undefined ? {} : { handlerConfig }),
  };
}
