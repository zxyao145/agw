"use client";

import * as React from "react";
import { Activity, CircleAlert, Clock3, Plus, RotateCcw, Trash2 } from "lucide-react";
import { toast } from "sonner";

import {
  apiGet,
  apiPost,
  getApiErrorMessage,
  type EventResponse,
  type EventSourceResponse,
  type EventSourceCreateRequest,
  type EventSourceUpdateRequest,
  type EventSubscriptionRequest,
  type EventSubscriptionResponse,
  type JsonValue,
  type UnpublishedEventRecord,
} from "@agw/api";
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
  formatLocalDateTime,
} from "@agw/components";
import { useMutation, useQuery, useQueryClient } from "@agw/components/query";
import { getManagedLabel } from "./managed-record";

type SourceFormState = {
  name: string;
  kind: number;
  configText: string;
  secretText: string;
  providerExtensionId: string;
  providerId: string;
};

type EventFilterState = { type?: string; subject?: string; chainId?: string };

type SubscriptionFormState = {
  name: string;
  enabled: boolean;
  sourceIds: string[];
  publisherKeys: string;
  eventTypes: string;
  targetKind: number;
  agentId: string;
  projectId: string;
  permissionMode: number;
  instructions: string;
  filterText: string;
  handlerExtensionId: string;
  handlerId: string;
  handlerConfigText: string;
};

const emptySourceForm: SourceFormState = {
  name: "",
  kind: 1,
  configText: "{}",
  secretText: "",
  providerExtensionId: "",
  providerId: "",
};

const EXECUTION_PERMISSION_MODES = {
  fullAccess: 0,
  alwaysAsk: 1,
  allowSameArguments: 2,
} as const;

const emptySubscriptionForm: SubscriptionFormState = {
  name: "",
  enabled: true,
  sourceIds: [],
  publisherKeys: "",
  eventTypes: "",
  targetKind: 0,
  agentId: "",
  projectId: "",
  permissionMode: EXECUTION_PERMISSION_MODES.fullAccess,
  instructions: "",
  filterText: "",
  handlerExtensionId: "",
  handlerId: "",
  handlerConfigText: "",
};

const NO_PROJECT = "none";

function parseJson(text: string): JsonValue | undefined {
  if (!text.trim()) return undefined;
  return JSON.parse(text) as JsonValue;
}

function splitValues(text: string): string[] {
  return text
    .split(",")
    .map((value) => value.trim())
    .filter(Boolean);
}

function eventStateLabel(value: number): string {
  return ["Pending", "Dispatched", "No match", "Hop limit exceeded"][value] ?? `State ${value}`;
}

function deliveryStateLabel(value: number): string {
  return (
    [
      "Pending",
      "Running",
      "Waiting for execution",
      "Succeeded",
      "Retry scheduled",
      "Held",
      "Parked",
      "Cancelled",
    ][value] ?? `State ${value}`
  );
}

function sourceKindLabel(value: number): string {
  return (
    ["Timer", "Webhook", "HTTP poll", "Extension provider", "Publisher"][value] ?? `Kind ${value}`
  );
}

function defaultSubscriptionForm(subscription: EventSubscriptionResponse): SubscriptionFormState {
  return {
    name: subscription.name,
    enabled: subscription.enabled,
    sourceIds: [...subscription.sourceIds],
    publisherKeys: subscription.publisherKeys.join(", "),
    eventTypes: subscription.eventTypes.join(", "),
    targetKind: subscription.targetKind,
    agentId: subscription.agentId ?? "",
    projectId: subscription.projectId ?? "",
    permissionMode: subscription.permissionMode ?? EXECUTION_PERMISSION_MODES.alwaysAsk,
    instructions: subscription.instructions ?? "",
    filterText: subscription.filter ? JSON.stringify(subscription.filter, null, 2) : "",
    handlerExtensionId: subscription.handlerExtensionId ?? "",
    handlerId: subscription.handlerId ?? "",
    handlerConfigText: subscription.handlerConfig
      ? JSON.stringify(subscription.handlerConfig, null, 2)
      : "",
  };
}

function sourceConfigText(source: EventSourceResponse): string {
  return JSON.stringify(source.config, null, 2);
}

function ManagedBadge({
  managedBy,
  managedKey,
}: {
  managedBy: string;
  managedKey?: string | null;
}) {
  const label = getManagedLabel(managedBy, managedKey);
  return label ? <Badge variant="outline">{label}</Badge> : null;
}

export function EventsPage() {
  const queryClient = useQueryClient();
  const [tab, setTab] = React.useState("events");
  const [filters, setFilters] = React.useState<EventFilterState>({});
  const [draftFilters, setDraftFilters] = React.useState<EventFilterState>({});
  const [selectedEventId, setSelectedEventId] = React.useState<string | null>(null);
  const [sourceDialogOpen, setSourceDialogOpen] = React.useState(false);
  const [editingSource, setEditingSource] = React.useState<EventSourceResponse | null>(null);
  const [sourceForm, setSourceForm] = React.useState<SourceFormState>(emptySourceForm);
  const [subscriptionDialogOpen, setSubscriptionDialogOpen] = React.useState(false);
  const [editingSubscription, setEditingSubscription] =
    React.useState<EventSubscriptionResponse | null>(null);
  const [subscriptionForm, setSubscriptionForm] =
    React.useState<SubscriptionFormState>(emptySubscriptionForm);

  const eventsQuery = useQuery({
    queryKey: ["events", "list", filters],
    queryFn: () =>
      apiGet("/api/events/list", {
        params: {
          query: {
            SourceId: undefined,
            Type: filters.type,
            Subject: filters.subject,
            ChainId: filters.chainId,
            From: undefined,
            To: undefined,
            Offset: 0,
            Limit: 100,
          },
        },
      }),
    enabled: tab === "events",
  });
  const eventQuery = useQuery({
    queryKey: ["events", "detail", selectedEventId],
    queryFn: () => apiGet("/api/events/get", { params: { query: { id: selectedEventId! } } }),
    enabled: Boolean(selectedEventId) && tab === "events",
  });
  const sourcesQuery = useQuery({
    queryKey: ["events", "sources"],
    queryFn: () => apiGet("/api/events/sources/list"),
    enabled: tab === "sources" || tab === "subscriptions",
  });
  const subscriptionsQuery = useQuery({
    queryKey: ["events", "subscriptions"],
    queryFn: () => apiGet("/api/events/subscriptions/list"),
    enabled: tab === "subscriptions",
  });
  const deliveriesQuery = useQuery({
    queryKey: ["events", "deliveries", selectedEventId],
    queryFn: () =>
      apiGet("/api/events/deliveries/list", {
        params: { query: { eventId: selectedEventId ?? undefined } },
      }),
    enabled: tab === "deliveries",
  });
  const deliveryActionMutation = useMutation({
    mutationFn: ({ action, id }: { action: "retry" | "cancel"; id: string }) =>
      action === "retry"
        ? apiPost("/api/events/deliveries/retry", { body: { id } })
        : apiPost("/api/events/deliveries/cancel", { body: { id } }),
    onSuccess: async (result, variables) => {
      if (result?.accepted) {
        toast.success(
          variables.action === "retry" ? "Delivery retry queued" : "Delivery cancelled",
        );
      } else {
        toast.info("No delivery state changed");
      }
      await queryClient.invalidateQueries({ queryKey: ["events", "deliveries"] });
    },
    onError: (error) => toast.error(`Delivery action failed: ${getApiErrorMessage(error)}`),
  });
  const schedulesQuery = useQuery({
    queryKey: ["events", "schedules"],
    queryFn: () => apiGet("/api/events/schedules/list"),
    enabled: tab === "schedules",
  });
  const unpublishedQuery = useQuery({
    queryKey: ["events", "unpublished"],
    queryFn: () => apiGet("/api/events/unpublished/list"),
    enabled: tab === "unpublished",
  });

  const invalidateEvents = async () => {
    await queryClient.invalidateQueries({ queryKey: ["events"] });
  };

  const createSourceMutation = useMutation({
    mutationFn: (body: EventSourceCreateRequest) => apiPost("/api/events/sources/create", { body }),
    onSuccess: async (result) => {
      toast.success(
        result?.secret ? `Source created. Save this secret: ${result.secret}` : "Source created",
      );
      setSourceDialogOpen(false);
      await invalidateEvents();
    },
    onError: (error) => toast.error(`Unable to create source: ${getApiErrorMessage(error)}`),
  });
  const updateSourceMutation = useMutation({
    mutationFn: (body: EventSourceUpdateRequest) => apiPost("/api/events/sources/update", { body }),
    onSuccess: async (result) => {
      toast.success(
        result?.secret ? `Source updated. Save this secret: ${result.secret}` : "Source updated",
      );
      setSourceDialogOpen(false);
      setEditingSource(null);
      await invalidateEvents();
    },
    onError: (error) => toast.error(`Unable to update source: ${getApiErrorMessage(error)}`),
  });
  const sourceActionMutation = useMutation({
    mutationFn: async ({
      action,
      source,
    }: {
      action: "delete" | "enable" | "rotate" | "test";
      source: EventSourceResponse;
    }) => {
      if (action === "delete")
        return apiPost("/api/events/sources/delete", {
          body: { id: source.id, version: source.version },
        });
      if (action === "enable")
        return apiPost("/api/events/sources/enable", {
          body: { id: source.id, version: source.version, enabled: !source.enabled },
        });
      if (action === "rotate")
        return apiPost("/api/events/sources/rotate-secret", {
          body: { id: source.id, version: source.version },
        });
      return apiPost("/api/events/sources/test", { body: { id: source.id } });
    },
    onSuccess: async (result, variables) => {
      if (
        variables.action === "test" &&
        typeof result === "object" &&
        result !== null &&
        "itemCount" in result
      )
        toast.success(`Source check returned ${result.itemCount} item(s)`);
      else if (
        variables.action === "rotate" &&
        typeof result === "object" &&
        result !== null &&
        "secret" in result
      )
        toast.success(`New source secret: ${result.secret ?? ""}`);
      else toast.success(variables.action === "delete" ? "Source deleted" : "Source updated");
      await invalidateEvents();
    },
    onError: (error) => toast.error(`Source action failed: ${getApiErrorMessage(error)}`),
  });
  const saveSubscriptionMutation = useMutation({
    mutationFn: (request: EventSubscriptionRequest) =>
      editingSubscription
        ? apiPost("/api/events/subscriptions/update", { body: request })
        : apiPost("/api/events/subscriptions/create", { body: request }),
    onSuccess: async () => {
      toast.success(editingSubscription ? "Subscription updated" : "Subscription created");
      setSubscriptionDialogOpen(false);
      setEditingSubscription(null);
      await invalidateEvents();
    },
    onError: (error) => toast.error(`Unable to save subscription: ${getApiErrorMessage(error)}`),
  });
  const subscriptionDeleteMutation = useMutation({
    mutationFn: (subscription: EventSubscriptionResponse) =>
      apiPost("/api/events/subscriptions/delete", {
        body: { id: subscription.id, version: subscription.version },
      }),
    onSuccess: async () => {
      toast.success("Subscription deleted");
      await invalidateEvents();
    },
    onError: (error) => toast.error(`Unable to delete subscription: ${getApiErrorMessage(error)}`),
  });
  const republishMutation = useMutation({
    mutationFn: (record: UnpublishedEventRecord) =>
      apiPost("/api/events/unpublished/republish", {
        body: { topic: record.topic, entityId: record.entityId },
      }),
    onSuccess: async () => {
      toast.success("Republish requested");
      await queryClient.invalidateQueries({ queryKey: ["events", "unpublished"] });
    },
    onError: (error) => toast.error(`Unable to republish record: ${getApiErrorMessage(error)}`),
  });

  const events = eventsQuery.data ?? [];
  const sources = sourcesQuery.data ?? [];
  const subscriptions = subscriptionsQuery.data ?? [];
  const deliveries = deliveriesQuery.data ?? [];
  const schedules = schedulesQuery.data ?? [];
  const unpublished = unpublishedQuery.data ?? [];

  const beginCreateSource = () => {
    setEditingSource(null);
    setSourceForm(emptySourceForm);
    setSourceDialogOpen(true);
  };

  const beginEditSource = (source: EventSourceResponse) => {
    setEditingSource(source);
    setSourceForm({
      name: source.name,
      kind: source.kind,
      configText: sourceConfigText(source),
      secretText: "",
      providerExtensionId: source.providerExtensionId ?? "",
      providerId: source.providerId ?? "",
    });
    setSourceDialogOpen(true);
  };

  const saveSource = () => {
    try {
      const config = parseJson(sourceForm.configText);
      if (config === undefined) throw new Error("Source configuration is required.");
      const secret = parseJson(sourceForm.secretText);
      if (editingSource) {
        updateSourceMutation.mutate({
          id: editingSource.id,
          version: editingSource.version,
          name: sourceForm.name.trim(),
          config,
          ...(secret === undefined ? {} : { secret }),
        });
      } else {
        if (
          sourceForm.kind === 3 &&
          (!sourceForm.providerExtensionId.trim() || !sourceForm.providerId.trim())
        ) {
          throw new Error("Extension ID and provider ID are required for an extension source.");
        }
        createSourceMutation.mutate({
          name: sourceForm.name.trim(),
          kind: sourceForm.kind,
          config,
          enabled: true,
          ...(sourceForm.kind === 3 && sourceForm.providerExtensionId.trim()
            ? { providerExtensionId: sourceForm.providerExtensionId.trim() }
            : {}),
          ...(sourceForm.kind === 3 && sourceForm.providerId.trim()
            ? { providerId: sourceForm.providerId.trim() }
            : {}),
          ...(secret === undefined ? {} : { secret }),
        });
      }
    } catch (error) {
      toast.error(`Source configuration is invalid: ${getApiErrorMessage(error)}`);
    }
  };

  const beginCreateSubscription = () => {
    setEditingSubscription(null);
    setSubscriptionForm(emptySubscriptionForm);
    setSubscriptionDialogOpen(true);
  };

  const beginEditSubscription = (subscription: EventSubscriptionResponse) => {
    setEditingSubscription(subscription);
    setSubscriptionForm(defaultSubscriptionForm(subscription));
    setSubscriptionDialogOpen(true);
  };

  const saveSubscription = () => {
    try {
      const filter = parseJson(subscriptionForm.filterText);
      const handlerConfig = parseJson(subscriptionForm.handlerConfigText);
      const isExecutionTarget = subscriptionForm.targetKind !== 2;
      const request: EventSubscriptionRequest = {
        ...(editingSubscription
          ? { id: editingSubscription.id, version: editingSubscription.version }
          : {}),
        name: subscriptionForm.name.trim(),
        enabled: subscriptionForm.enabled,
        sourceIds: subscriptionForm.sourceIds,
        publisherKeys: splitValues(subscriptionForm.publisherKeys),
        eventTypes: splitValues(subscriptionForm.eventTypes),
        ...(filter === undefined ? {} : { filter }),
        targetKind: subscriptionForm.targetKind,
        agentId: isExecutionTarget ? subscriptionForm.agentId || null : null,
        projectId: subscriptionForm.projectId || null,
        permissionMode: isExecutionTarget ? subscriptionForm.permissionMode : null,
        instructions: subscriptionForm.instructions.trim() || null,
        handlerExtensionId:
          subscriptionForm.targetKind === 2
            ? subscriptionForm.handlerExtensionId.trim() || null
            : null,
        handlerId:
          subscriptionForm.targetKind === 2 ? subscriptionForm.handlerId.trim() || null : null,
        ...(subscriptionForm.targetKind === 2 && handlerConfig !== undefined
          ? { handlerConfig }
          : {}),
      };
      if (!request.name || request.eventTypes.length === 0)
        throw new Error("Name and at least one event type are required.");
      if (isExecutionTarget && (!request.agentId || !request.projectId))
        throw new Error("Select an Agent or Agentflow and a project.");
      if (request.targetKind === 2 && (!request.handlerExtensionId || !request.handlerId)) {
        throw new Error(
          "Extension ID and handler ID are required for an extension handler target.",
        );
      }
      saveSubscriptionMutation.mutate(request);
    } catch (error) {
      toast.error(`Subscription configuration is invalid: ${getApiErrorMessage(error)}`);
    }
  };

  return (
    <section className="w-full space-y-5 p-4 sm:p-6">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div className="space-y-1">
          <div className="flex items-center gap-2">
            <Activity className="size-5 text-primary" />
            <h1 className="text-xl font-semibold">Events</h1>
          </div>
          <p className="text-sm text-muted-foreground">
            Inspect event flow, configure sources and subscriptions, and review deliveries.
          </p>
        </div>
        <Button type="button" size="sm" variant="outline" onClick={() => void invalidateEvents()}>
          <RotateCcw className="size-4" />
          Refresh
        </Button>
      </header>

      <Tabs value={tab} onValueChange={setTab}>
        <TabsList className="h-auto flex-wrap justify-start">
          <TabsTrigger value="events">Events</TabsTrigger>
          <TabsTrigger value="sources">Sources</TabsTrigger>
          <TabsTrigger value="subscriptions">Subscriptions</TabsTrigger>
          <TabsTrigger value="deliveries">Deliveries</TabsTrigger>
          <TabsTrigger value="schedules">Schedules</TabsTrigger>
          <TabsTrigger value="unpublished">Waiting records</TabsTrigger>
        </TabsList>

        <TabsContent value="events" className="space-y-4">
          <Card>
            <CardContent className="grid gap-3 p-4 md:grid-cols-[1fr_1fr_1fr_auto]">
              <div className="space-y-1">
                <Label htmlFor="event-filter-type">Event type</Label>
                <Input
                  id="event-filter-type"
                  value={draftFilters.type ?? ""}
                  onChange={(event) =>
                    setDraftFilters((current) => ({
                      ...current,
                      type: event.target.value || undefined,
                    }))
                  }
                  placeholder="agw.execution.completed"
                />
              </div>
              <div className="space-y-1">
                <Label htmlFor="event-filter-subject">Subject</Label>
                <Input
                  id="event-filter-subject"
                  value={draftFilters.subject ?? ""}
                  onChange={(event) =>
                    setDraftFilters((current) => ({
                      ...current,
                      subject: event.target.value || undefined,
                    }))
                  }
                />
              </div>
              <div className="space-y-1">
                <Label htmlFor="event-filter-chain">Chain ID</Label>
                <Input
                  id="event-filter-chain"
                  value={draftFilters.chainId ?? ""}
                  onChange={(event) =>
                    setDraftFilters((current) => ({
                      ...current,
                      chainId: event.target.value || undefined,
                    }))
                  }
                />
              </div>
              <div className="flex items-end">
                <Button type="button" onClick={() => setFilters({ ...draftFilters })}>
                  Apply filters
                </Button>
              </div>
            </CardContent>
          </Card>
          <div className="grid gap-4 xl:grid-cols-[minmax(0,1.15fr)_minmax(20rem,0.85fr)]">
            <Card className="min-h-0 overflow-hidden">
              <CardHeader className="border-b py-4">
                <CardTitle className="text-base">Event stream</CardTitle>
                <CardDescription>{events.length} events</CardDescription>
              </CardHeader>
              <CardContent className="max-h-[38rem] overflow-auto p-0">
                {eventsQuery.isPending ? (
                  <p className="p-5 text-sm text-muted-foreground" role="status">
                    Loading events…
                  </p>
                ) : eventsQuery.isError ? (
                  <p className="p-5 text-sm text-destructive" role="alert">
                    {getApiErrorMessage(eventsQuery.error)}
                  </p>
                ) : events.length === 0 ? (
                  <p className="p-5 text-sm text-muted-foreground">
                    No events match these filters.
                  </p>
                ) : (
                  <ul className="divide-y">
                    {events.map((event) => (
                      <li key={event.id}>
                        <button
                          type="button"
                          className={`w-full px-4 py-3 text-left outline-none hover:bg-muted/50 focus-visible:ring-2 focus-visible:ring-ring ${selectedEventId === event.id ? "bg-muted/60" : ""}`}
                          onClick={() => setSelectedEventId(event.id)}
                        >
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <span className="break-all text-sm font-medium">
                              {event.cloudEvent.type}
                            </span>
                            <Badge
                              variant={
                                event.dispatchState === 1
                                  ? "secondary"
                                  : event.dispatchState === 3
                                    ? "destructive"
                                    : "outline"
                              }
                            >
                              {eventStateLabel(event.dispatchState)}
                            </Badge>
                          </div>
                          <div className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
                            <span>{event.cloudEvent.subject ?? "No subject"}</span>
                            <span>{formatLocalDateTime(event.receivedAt)}</span>
                            <span>{event.matchedCount} matches</span>
                          </div>
                          <div className="mt-1 font-mono text-[10px] text-muted-foreground">
                            Chain {event.chainId} · hop {event.hops}
                          </div>
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </CardContent>
            </Card>
            <Card className="min-h-0 overflow-hidden">
              <CardHeader className="border-b py-4">
                <CardTitle className="text-base">Event details</CardTitle>
                <CardDescription>
                  {eventQuery.data?.id ?? "Select an event to inspect its payload and chain."}
                </CardDescription>
              </CardHeader>
              <CardContent className="max-h-[38rem] overflow-auto p-4">
                {eventQuery.isPending ? (
                  <p className="text-sm text-muted-foreground" role="status">
                    Loading event…
                  </p>
                ) : eventQuery.isError ? (
                  <p className="text-sm text-destructive" role="alert">
                    {getApiErrorMessage(eventQuery.error)}
                  </p>
                ) : eventQuery.data ? (
                  <EventDetails
                    event={eventQuery.data}
                    onOpenChain={(chainId) => {
                      setDraftFilters((current) => ({ ...current, chainId }));
                      setFilters((current) => ({ ...current, chainId }));
                    }}
                  />
                ) : (
                  <p className="text-sm text-muted-foreground">Select an event from the stream.</p>
                )}
              </CardContent>
            </Card>
          </div>
        </TabsContent>

        <TabsContent value="sources" className="space-y-4">
          <Card>
            <CardHeader className="flex flex-row items-center justify-between gap-3">
              <div>
                <CardTitle className="text-base">Sources</CardTitle>
                <CardDescription>Bring external events into Agw.</CardDescription>
              </div>
              <Button size="sm" onClick={beginCreateSource}>
                <Plus className="size-4" />
                Add source
              </Button>
            </CardHeader>
            <CardContent className="space-y-3">
              {sourcesQuery.isPending ? (
                <p className="text-sm text-muted-foreground" role="status">
                  Loading sources…
                </p>
              ) : sourcesQuery.isError ? (
                <p className="text-sm text-destructive" role="alert">
                  {getApiErrorMessage(sourcesQuery.error)}
                </p>
              ) : sources.length === 0 ? (
                <p className="text-sm text-muted-foreground">No event sources configured.</p>
              ) : (
                sources.map((source) => (
                  <div
                    key={source.id}
                    className="flex flex-wrap items-center justify-between gap-3 rounded-md border p-3"
                  >
                    <div className="min-w-0 space-y-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="font-medium">{source.name}</span>
                        <Badge variant={source.enabled ? "secondary" : "outline"}>
                          {source.enabled ? "Enabled" : "Disabled"}
                        </Badge>
                        <Badge variant="outline">{sourceKindLabel(source.kind)}</Badge>
                        <ManagedBadge managedBy={source.managedBy} managedKey={source.managedKey} />
                      </div>
                      <p className="break-all font-mono text-xs text-muted-foreground">
                        {source.ceSource}
                      </p>
                      <p className="text-xs text-muted-foreground">
                        {source.lastRunAt
                          ? `Last run ${formatLocalDateTime(source.lastRunAt)}`
                          : "No runs yet"}
                        {source.nextRunAt ? ` · Next ${formatLocalDateTime(source.nextRunAt)}` : ""}
                      </p>
                      {source.lastError ? (
                        <p className="break-words text-xs text-destructive">{source.lastError}</p>
                      ) : null}
                    </div>
                    {getManagedLabel(source.managedBy, source.managedKey) ? (
                      <Badge variant="outline">Read only</Badge>
                    ) : (
                      <div className="flex flex-wrap gap-2">
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => sourceActionMutation.mutate({ action: "test", source })}
                        >
                          Test
                        </Button>
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => sourceActionMutation.mutate({ action: "enable", source })}
                        >
                          {source.enabled ? "Disable" : "Enable"}
                        </Button>
                        <Button size="sm" variant="outline" onClick={() => beginEditSource(source)}>
                          Edit
                        </Button>
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => sourceActionMutation.mutate({ action: "rotate", source })}
                        >
                          Rotate secret
                        </Button>
                        <Button
                          size="sm"
                          variant="destructive"
                          aria-label={`Delete ${source.name}`}
                          onClick={() => {
                            if (window.confirm(`Delete source “${source.name}”?`))
                              sourceActionMutation.mutate({ action: "delete", source });
                          }}
                        >
                          <Trash2 className="size-4" />
                        </Button>
                      </div>
                    )}
                  </div>
                ))
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="subscriptions" className="space-y-4">
          <Card>
            <CardHeader className="flex flex-row items-center justify-between gap-3">
              <div>
                <CardTitle className="text-base">Subscriptions</CardTitle>
                <CardDescription>
                  Match incoming events to agents, agentflows, or extension handlers.
                </CardDescription>
              </div>
              <Button size="sm" onClick={beginCreateSubscription}>
                <Plus className="size-4" />
                Add subscription
              </Button>
            </CardHeader>
            <CardContent className="space-y-3">
              {subscriptionsQuery.isPending ? (
                <p className="text-sm text-muted-foreground" role="status">
                  Loading subscriptions…
                </p>
              ) : subscriptionsQuery.isError ? (
                <p className="text-sm text-destructive" role="alert">
                  {getApiErrorMessage(subscriptionsQuery.error)}
                </p>
              ) : subscriptions.length === 0 ? (
                <p className="text-sm text-muted-foreground">No event subscriptions configured.</p>
              ) : (
                subscriptions.map((subscription) => (
                  <div
                    key={subscription.id}
                    className="flex flex-wrap items-center justify-between gap-3 rounded-md border p-3"
                  >
                    <div className="min-w-0 space-y-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="font-medium">{subscription.name}</span>
                        <Badge variant={subscription.enabled ? "secondary" : "outline"}>
                          {subscription.enabled ? "Enabled" : "Disabled"}
                        </Badge>
                        <ManagedBadge
                          managedBy={subscription.managedBy}
                          managedKey={subscription.managedKey}
                        />
                      </div>
                      <p className="break-words text-xs text-muted-foreground">
                        {subscription.eventTypes.join(", ")}
                      </p>
                      <p className="font-mono text-[10px] text-muted-foreground">
                        Target {subscription.targetKind} · v{subscription.version}
                      </p>
                    </div>
                    {getManagedLabel(subscription.managedBy, subscription.managedKey) ? (
                      <Badge variant="outline">Read only</Badge>
                    ) : (
                      <div className="flex gap-2">
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => beginEditSubscription(subscription)}
                        >
                          Edit
                        </Button>
                        <Button
                          size="sm"
                          variant="destructive"
                          onClick={() => {
                            if (window.confirm(`Delete subscription “${subscription.name}”?`))
                              subscriptionDeleteMutation.mutate(subscription);
                          }}
                        >
                          Delete
                        </Button>
                      </div>
                    )}
                  </div>
                ))
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="deliveries">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Delivery progress</CardTitle>
              <CardDescription>
                {selectedEventId
                  ? `Deliveries for event ${selectedEventId}`
                  : "Recent delivery attempts across events."}
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              <div className="max-w-lg space-y-1">
                <Label htmlFor="delivery-event-id">Filter by event ID</Label>
                <Input
                  id="delivery-event-id"
                  value={selectedEventId ?? ""}
                  onChange={(event) => setSelectedEventId(event.target.value.trim() || null)}
                  placeholder="Event ID"
                />
              </div>
              {deliveriesQuery.isPending ? (
                <p className="text-sm text-muted-foreground" role="status">
                  Loading deliveries…
                </p>
              ) : deliveriesQuery.isError ? (
                <p className="text-sm text-destructive" role="alert">
                  {getApiErrorMessage(deliveriesQuery.error)}
                </p>
              ) : deliveries.length === 0 ? (
                <p className="text-sm text-muted-foreground">No delivery records found.</p>
              ) : (
                <div className="space-y-2">
                  {deliveries.map((delivery) => (
                    <div
                      key={delivery.id}
                      className="grid gap-2 rounded-md border p-3 sm:grid-cols-[1fr_auto]"
                    >
                      <div className="min-w-0 space-y-1">
                        <p className="break-all font-mono text-xs">{delivery.id}</p>
                        <p className="text-xs text-muted-foreground">
                          Event {delivery.eventId} · Subscription {delivery.subscriptionId}
                        </p>
                        {delivery.lastError ? (
                          <p className="break-words text-xs text-destructive">
                            {delivery.lastError}
                          </p>
                        ) : null}
                      </div>
                      <div className="flex flex-wrap items-center gap-2">
                        <Badge
                          variant={
                            delivery.state === 3
                              ? "secondary"
                              : delivery.state === 6
                                ? "destructive"
                                : "outline"
                          }
                        >
                          {deliveryStateLabel(delivery.state)}
                        </Badge>
                        <span className="text-xs text-muted-foreground">
                          Attempt {delivery.attempt}
                        </span>
                        {delivery.state === 6 ? (
                          <Button
                            size="sm"
                            variant="outline"
                            onClick={() =>
                              deliveryActionMutation.mutate({ action: "retry", id: delivery.id })
                            }
                            disabled={deliveryActionMutation.isPending}
                          >
                            Retry
                          </Button>
                        ) : null}
                        {![3, 6, 7].includes(delivery.state) ? (
                          <Button
                            size="sm"
                            variant="outline"
                            onClick={() =>
                              deliveryActionMutation.mutate({ action: "cancel", id: delivery.id })
                            }
                            disabled={deliveryActionMutation.isPending}
                          >
                            Cancel
                          </Button>
                        ) : null}
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="schedules">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Schedules</CardTitle>
              <CardDescription>Upcoming event timers and recurring schedules.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {schedulesQuery.isPending ? (
                <p className="text-sm text-muted-foreground" role="status">
                  Loading schedules…
                </p>
              ) : schedulesQuery.isError ? (
                <p className="text-sm text-destructive" role="alert">
                  {getApiErrorMessage(schedulesQuery.error)}
                </p>
              ) : schedules.length === 0 ? (
                <p className="text-sm text-muted-foreground">No scheduled events.</p>
              ) : (
                schedules.map((schedule) => (
                  <div
                    key={schedule.id}
                    className="flex flex-wrap items-center justify-between gap-3 rounded-md border p-3"
                  >
                    <div className="space-y-1">
                      <div className="flex items-center gap-2">
                        <Clock3 className="size-4 text-muted-foreground" />
                        <span className="font-medium">
                          {schedule.summary ??
                            schedule.eventType ??
                            schedule.subject ??
                            "Scheduled event"}
                        </span>
                        <Badge variant="outline">
                          {schedule.state === 0
                            ? "Active"
                            : schedule.state === 1
                              ? "Completed"
                              : "Cancelled"}
                        </Badge>
                      </div>
                      <p className="text-xs text-muted-foreground">
                        {schedule.cron ?? schedule.interval ?? schedule.dueAt ?? "No due time"}
                        {schedule.timeZone ? ` · ${schedule.timeZone}` : ""}
                      </p>
                      <p className="font-mono text-[10px] text-muted-foreground">
                        Event {schedule.eventType ?? "unknown"} · occurrence {schedule.occurrence}
                      </p>
                    </div>
                    <span className="text-xs text-muted-foreground">
                      {schedule.dueAt ? formatLocalDateTime(schedule.dueAt) : "Recurring"}
                    </span>
                  </div>
                ))
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="unpublished">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Waiting records</CardTitle>
              <CardDescription>
                Records waiting past their recovery time can be republished here.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {unpublishedQuery.isPending ? (
                <p className="text-sm text-muted-foreground" role="status">
                  Loading records…
                </p>
              ) : unpublishedQuery.isError ? (
                <p className="text-sm text-destructive" role="alert">
                  {getApiErrorMessage(unpublishedQuery.error)}
                </p>
              ) : unpublished.length === 0 ? (
                <p className="text-sm text-muted-foreground">
                  No overdue event or delivery records.
                </p>
              ) : (
                unpublished.map((record) => (
                  <div
                    key={`${record.topic}:${record.entityId}`}
                    className="flex flex-wrap items-center justify-between gap-3 rounded-md border p-3"
                  >
                    <div className="min-w-0">
                      <div className="flex items-center gap-2">
                        <CircleAlert className="size-4 text-amber-600" />
                        <span className="font-medium">{record.topic}</span>
                        <Badge variant="outline">{record.state}</Badge>
                      </div>
                      <p className="mt-1 break-all font-mono text-xs text-muted-foreground">
                        {record.entityId}
                      </p>
                      <p className="mt-1 text-xs text-muted-foreground">
                        Recover after {formatLocalDateTime(record.recoverAfter)}
                      </p>
                    </div>
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => republishMutation.mutate(record)}
                      disabled={republishMutation.isPending}
                    >
                      <RotateCcw className="size-4" />
                      Republish
                    </Button>
                  </div>
                ))
              )}
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>

      <SourceDialog
        open={sourceDialogOpen}
        editingSource={editingSource}
        form={sourceForm}
        pending={createSourceMutation.isPending || updateSourceMutation.isPending}
        onFormChange={setSourceForm}
        onOpenChange={setSourceDialogOpen}
        onSubmit={saveSource}
      />
      <SubscriptionDialog
        open={subscriptionDialogOpen}
        editingSubscription={editingSubscription}
        form={subscriptionForm}
        sources={sources}
        pending={saveSubscriptionMutation.isPending}
        onFormChange={setSubscriptionForm}
        onOpenChange={setSubscriptionDialogOpen}
        onSubmit={saveSubscription}
      />
    </section>
  );
}

function EventDetails({
  event,
  onOpenChain,
}: {
  event: EventResponse;
  onOpenChain(chainId: string): void;
}) {
  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2">
        <DetailValue label="Source" value={event.sourceId} />
        <DetailValue label="Received" value={formatLocalDateTime(event.receivedAt)} />
        <DetailValue label="Dispatch" value={eventStateLabel(event.dispatchState)} />
        <DetailValue label="Matched" value={String(event.matchedCount)} />
        {event.summary ? <DetailValue label="Summary" value={event.summary} /> : null}
        {event.link ? <DetailValue label="Link" value={event.link} /> : null}
      </div>
      <div className="space-y-2">
        <div className="flex items-center justify-between gap-2">
          <Label>Processing chain</Label>
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => onOpenChain(event.chainId)}
          >
            Show chain
          </Button>
        </div>
        <p className="break-all font-mono text-xs text-muted-foreground">
          {event.chainId} · hop {event.hops}
        </p>
      </div>
      <div className="space-y-2">
        <Label>CloudEvent</Label>
        <pre className="max-h-80 overflow-auto rounded-md border bg-muted/30 p-3 text-xs leading-5">
          {JSON.stringify(event.cloudEvent, null, 2)}
        </pre>
      </div>
    </div>
  );
}

function DetailValue({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0 space-y-1">
      <Label>{label}</Label>
      <p className="break-all rounded-md border bg-muted/30 px-3 py-2 text-xs text-muted-foreground">
        {value}
      </p>
    </div>
  );
}

function SourceDialog({
  open,
  editingSource,
  form,
  pending,
  onFormChange,
  onOpenChange,
  onSubmit,
}: {
  open: boolean;
  editingSource: EventSourceResponse | null;
  form: SourceFormState;
  pending: boolean;
  onFormChange: React.Dispatch<React.SetStateAction<SourceFormState>>;
  onOpenChange(open: boolean): void;
  onSubmit(): void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="2xl" className="max-h-[90vh] overflow-hidden">
        <DialogHeader>
          <DialogTitle>{editingSource ? "Edit source" : "Add source"}</DialogTitle>
          <DialogDescription>
            Configure how Agw receives events. Secret values are only returned when a source is
            created or rotated.
          </DialogDescription>
        </DialogHeader>
        <div className="max-h-[65vh] space-y-4 overflow-y-auto pr-1">
          <div className="space-y-2">
            <Label htmlFor="source-name">Name</Label>
            <Input
              id="source-name"
              value={form.name}
              onChange={(event) =>
                onFormChange((current) => ({ ...current, name: event.target.value }))
              }
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="source-kind">Source type</Label>
            <select
              id="source-kind"
              className="h-9 w-full rounded-md border bg-background px-3 text-sm"
              value={String(form.kind)}
              disabled={Boolean(editingSource)}
              onChange={(event) =>
                onFormChange((current) => ({ ...current, kind: Number(event.target.value) }))
              }
            >
              <option value="1">Webhook</option>
              <option value="2">HTTP poll</option>
              <option value="3">Extension provider</option>
            </select>
          </div>
          {form.kind === 3 ? (
            <>
              <div className="space-y-2">
                <Label htmlFor="source-provider-extension">Extension ID</Label>
                <Input
                  id="source-provider-extension"
                  value={form.providerExtensionId}
                  disabled={Boolean(editingSource)}
                  onChange={(event) =>
                    onFormChange((current) => ({
                      ...current,
                      providerExtensionId: event.target.value,
                    }))
                  }
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="source-provider-id">Provider ID</Label>
                <Input
                  id="source-provider-id"
                  value={form.providerId}
                  disabled={Boolean(editingSource)}
                  onChange={(event) =>
                    onFormChange((current) => ({ ...current, providerId: event.target.value }))
                  }
                />
              </div>
            </>
          ) : null}
          <div className="space-y-2">
            <Label htmlFor="source-config">Configuration JSON</Label>
            <Textarea
              id="source-config"
              rows={8}
              className="font-mono text-xs"
              value={form.configText}
              onChange={(event) =>
                onFormChange((current) => ({ ...current, configText: event.target.value }))
              }
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="source-secret">Secret JSON</Label>
            <Textarea
              id="source-secret"
              rows={3}
              className="font-mono text-xs"
              value={form.secretText}
              onChange={(event) =>
                onFormChange((current) => ({ ...current, secretText: event.target.value }))
              }
              placeholder="Leave blank to keep the current value"
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={pending}>
            Cancel
          </Button>
          <Button onClick={onSubmit} disabled={pending || !form.name.trim()}>
            {pending ? "Saving…" : "Save source"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function SubscriptionDialog({
  open,
  editingSubscription,
  form,
  sources,
  pending,
  onFormChange,
  onOpenChange,
  onSubmit,
}: {
  open: boolean;
  editingSubscription: EventSubscriptionResponse | null;
  form: SubscriptionFormState;
  sources: EventSourceResponse[];
  pending: boolean;
  onFormChange: React.Dispatch<React.SetStateAction<SubscriptionFormState>>;
  onOpenChange(open: boolean): void;
  onSubmit(): void;
}) {
  const isAgentflow = form.targetKind === 1;
  const isExtension = form.targetKind === 2;
  const targetLabel = isAgentflow ? "Agentflow" : "Agent";
  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: () => apiGet("/api/agents"),
    enabled: open && form.targetKind === 0,
  });
  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: () => apiGet("/api/agentflows"),
    enabled: open && isAgentflow,
  });
  const projectsQuery = useQuery({
    queryKey: ["projects"],
    queryFn: () => apiGet("/api/projects"),
    enabled: open,
  });

  const targetsQuery = isAgentflow ? agentflowsQuery : agentsQuery;
  const targets = targetsQuery.data ?? [];
  const projects = projectsQuery.data ?? [];
  const hasTarget = targets.some((target) => target.id === form.agentId);
  const hasProject = projects.some((project) => project.id === form.projectId);
  const useNoProject = isExtension && !form.projectId;
  const canSave =
    !pending &&
    form.name.trim().length > 0 &&
    (isExtension || hasTarget) &&
    (useNoProject || hasProject);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="2xl" className="max-h-[90vh] overflow-hidden">
        <DialogHeader>
          <DialogTitle>
            {editingSubscription ? "Edit subscription" : "Add subscription"}
          </DialogTitle>
          <DialogDescription>
            Choose event types and the target that will process matching events.
          </DialogDescription>
        </DialogHeader>
        <div className="grid max-h-[65vh] gap-4 overflow-y-auto pr-1 md:grid-cols-2">
          <div className="space-y-2 md:col-span-2">
            <Label htmlFor="subscription-name">Name</Label>
            <Input
              id="subscription-name"
              value={form.name}
              onChange={(event) =>
                onFormChange((current) => ({ ...current, name: event.target.value }))
              }
            />
          </div>
          <div className="space-y-2 md:col-span-2">
            <Label htmlFor="subscription-event-types">Event types</Label>
            <Input
              id="subscription-event-types"
              value={form.eventTypes}
              onChange={(event) =>
                onFormChange((current) => ({ ...current, eventTypes: event.target.value }))
              }
              placeholder="taskpilot.review.due, agw.execution.completed"
            />
            <p className="text-xs text-muted-foreground">Separate types with commas.</p>
          </div>
          <div className="space-y-2 md:col-span-2">
            <Label htmlFor="subscription-publishers">Publisher keys</Label>
            <Input
              id="subscription-publishers"
              value={form.publisherKeys}
              onChange={(event) =>
                onFormChange((current) => ({ ...current, publisherKeys: event.target.value }))
              }
              placeholder="Optional publisher keys, separated with commas"
            />
          </div>
          <fieldset className="space-y-2 md:col-span-2">
            <legend className="text-sm font-medium">Sources</legend>
            <div className="grid gap-2 sm:grid-cols-2">
              {sources.map((source) => (
                <label
                  key={source.id}
                  className="flex items-center gap-2 rounded-md border px-3 py-2 text-sm"
                >
                  <input
                    type="checkbox"
                    checked={form.sourceIds.includes(source.id)}
                    onChange={(event) =>
                      onFormChange((current) => ({
                        ...current,
                        sourceIds: event.target.checked
                          ? [...current.sourceIds, source.id]
                          : current.sourceIds.filter((id) => id !== source.id),
                      }))
                    }
                    className="accent-primary"
                  />
                  <span className="truncate">{source.name}</span>
                </label>
              ))}
            </div>
            <p className="text-xs text-muted-foreground">Leave empty to match publisher keys.</p>
          </fieldset>
          <div className="space-y-2">
            <Label htmlFor="subscription-target">Target type</Label>
            <select
              id="subscription-target"
              className="h-9 w-full rounded-md border bg-background px-3 text-sm"
              value={String(form.targetKind)}
              onChange={(event) =>
                onFormChange((current) => ({
                  ...current,
                  targetKind: Number(event.target.value),
                  agentId: "",
                }))
              }
            >
              <option value="0">Agent</option>
              <option value="1">Agentflow</option>
              <option value="2">Extension handler</option>
            </select>
          </div>
          {!isExtension ? (
            <div className="space-y-2">
              <Label htmlFor="subscription-agent">{targetLabel}</Label>
              <Select
                value={hasTarget ? form.agentId : ""}
                required
                disabled={pending || targets.length === 0}
                onValueChange={(value) =>
                  onFormChange((current) => ({ ...current, agentId: value }))
                }
              >
                <SelectTrigger id="subscription-agent" className="w-full">
                  <SelectValue
                    placeholder={
                      targetsQuery.isPending
                        ? `Loading ${targetLabel}s…`
                        : targetsQuery.isError
                          ? `Unable to load ${targetLabel}s`
                          : targets.length === 0
                            ? `No ${targetLabel}s available`
                            : `Select an ${targetLabel}`
                    }
                  />
                </SelectTrigger>
                <SelectContent position="popper" align="start">
                  {targets.map((target) => (
                    <SelectItem key={target.id} value={target.id}>
                      {("displayName" in target && target.displayName?.trim()) || target.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          ) : (
            <>
              <div className="space-y-2">
                <Label htmlFor="subscription-handler-extension">Handler extension ID</Label>
                <Input
                  id="subscription-handler-extension"
                  value={form.handlerExtensionId}
                  onChange={(event) =>
                    onFormChange((current) => ({
                      ...current,
                      handlerExtensionId: event.target.value,
                    }))
                  }
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="subscription-handler-id">Handler ID</Label>
                <Input
                  id="subscription-handler-id"
                  value={form.handlerId}
                  onChange={(event) =>
                    onFormChange((current) => ({ ...current, handlerId: event.target.value }))
                  }
                />
              </div>
              <div className="space-y-2 md:col-span-2">
                <Label htmlFor="subscription-handler-config">Handler configuration JSON</Label>
                <Textarea
                  id="subscription-handler-config"
                  rows={4}
                  className="font-mono text-xs"
                  value={form.handlerConfigText}
                  onChange={(event) =>
                    onFormChange((current) => ({
                      ...current,
                      handlerConfigText: event.target.value,
                    }))
                  }
                />
              </div>
            </>
          )}
          <div className="space-y-2">
            <Label htmlFor="subscription-project">Project</Label>
            <Select
              value={useNoProject ? NO_PROJECT : hasProject ? form.projectId : ""}
              required={!isExtension}
              disabled={pending || (!isExtension && projects.length === 0)}
              onValueChange={(value) =>
                onFormChange((current) => ({
                  ...current,
                  projectId: value === NO_PROJECT ? "" : value,
                }))
              }
            >
              <SelectTrigger id="subscription-project" className="w-full">
                <SelectValue
                  placeholder={
                    projectsQuery.isPending
                      ? "Loading projects…"
                      : projectsQuery.isError
                        ? "Unable to load projects"
                        : projects.length === 0
                          ? "No projects available"
                          : "Select a project"
                  }
                />
              </SelectTrigger>
              <SelectContent position="popper" align="start">
                {isExtension ? <SelectItem value={NO_PROJECT}>No project</SelectItem> : null}
                {projects.map((project) => (
                  <SelectItem key={project.id} value={project.id}>
                    {project.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          {!isExtension ? (
            <div className="space-y-2">
              <Label htmlFor="subscription-permission">Execution permission</Label>
              <Select
                value={String(form.permissionMode)}
                disabled={pending}
                onValueChange={(value) =>
                  onFormChange((current) => ({ ...current, permissionMode: Number(value) }))
                }
              >
                <SelectTrigger id="subscription-permission" className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent position="popper" align="start">
                  <SelectItem value={String(EXECUTION_PERMISSION_MODES.fullAccess)}>
                    Full access
                  </SelectItem>
                  <SelectItem value={String(EXECUTION_PERMISSION_MODES.alwaysAsk)}>
                    Always ask
                  </SelectItem>
                  <SelectItem value={String(EXECUTION_PERMISSION_MODES.allowSameArguments)}>
                    Allow same arguments
                  </SelectItem>
                </SelectContent>
              </Select>
            </div>
          ) : null}
          <div className="space-y-2 md:col-span-2">
            <Label htmlFor="subscription-instructions">Instructions</Label>
            <Textarea
              id="subscription-instructions"
              rows={3}
              value={form.instructions}
              onChange={(event) =>
                onFormChange((current) => ({ ...current, instructions: event.target.value }))
              }
            />
          </div>
          <div className="space-y-2 md:col-span-2">
            <Label htmlFor="subscription-filter">Filter JSON</Label>
            <Textarea
              id="subscription-filter"
              rows={5}
              className="font-mono text-xs"
              value={form.filterText}
              onChange={(event) =>
                onFormChange((current) => ({ ...current, filterText: event.target.value }))
              }
              placeholder="Optional JsonLogic expression"
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={pending}>
            Cancel
          </Button>
          <Button onClick={onSubmit} disabled={!canSave}>
            {pending ? "Saving…" : "Save subscription"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
