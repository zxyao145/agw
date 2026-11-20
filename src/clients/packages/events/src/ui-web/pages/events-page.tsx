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
  Input,
  Label,
  TablePagination,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  formatLocalDateTime,
} from "@agw/components";
import { useMutation, useQuery, useQueryClient } from "@agw/components/query";
import { getManagedLabel, isUserManaged } from "../managed-record";
import {
  EVENT_PAGE_SIZE_OPTIONS,
  useEventListPage,
  type EventListPage,
} from "./use-event-list-page";
import { SourceDialog } from "../sources/source-dialog";
import { EVENT_SOURCE_KIND } from "../sources/source-name";
import {
  createSourceForm,
  InvalidSourceConfigurationError,
  sourceFormToCreateRequest,
  sourceFormToRequest,
  sourceToForm,
  type CreatedWebhook,
  type SourceFormOptions,
  type SourceFormState,
} from "../sources/source-form";
import { SubscriptionDialog } from "../subscriptions/subscription-dialog";
import {
  createSubscriptionForm,
  subscriptionFormToRequest,
  subscriptionToForm,
  type SubscriptionFormState,
} from "../subscriptions/subscription-form";

type EventFilterState = { type?: string; subject?: string; chainId?: string };

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
  const [sourceForm, setSourceForm] = React.useState<SourceFormState>(() => createSourceForm());
  const [createdSource, setCreatedSource] = React.useState<CreatedWebhook | null>(null);
  const [subscriptionDialogOpen, setSubscriptionDialogOpen] = React.useState(false);
  const [editingSubscription, setEditingSubscription] =
    React.useState<EventSubscriptionResponse | null>(null);
  const [subscriptionForm, setSubscriptionForm] = React.useState<SubscriptionFormState>(() =>
    createSubscriptionForm(),
  );

  const eventsPage = useEventListPage({
    queryKey: ["events", "list", filters],
    enabled: tab === "events",
    fetchPage: ({ pageIndex, pageSize }) =>
      apiGet("/api/events/list", {
        params: {
          query: {
            Type: filters.type,
            Subject: filters.subject,
            ChainId: filters.chainId,
            PageIndex: pageIndex,
            PageSize: pageSize,
          },
        },
      }),
  });
  const eventQuery = useQuery({
    queryKey: ["events", "detail", selectedEventId],
    queryFn: () => apiGet("/api/events/get", { params: { query: { id: selectedEventId! } } }),
    enabled: Boolean(selectedEventId) && tab === "events",
  });
  const sourcesPage = useEventListPage({
    queryKey: ["events", "sources"],
    enabled: tab === "sources",
    fetchPage: (query) => apiGet("/api/events/sources/list", { params: { query } }),
  });
  const subscriptionsPage = useEventListPage({
    queryKey: ["events", "subscriptions"],
    enabled: tab === "subscriptions",
    fetchPage: (query) => apiGet("/api/events/subscriptions/list", { params: { query } }),
  });
  const deliveriesPage = useEventListPage({
    queryKey: ["events", "deliveries", selectedEventId],
    enabled: tab === "deliveries",
    fetchPage: (query) =>
      apiGet("/api/events/deliveries/list", {
        params: { query: { ...query, eventId: selectedEventId ?? undefined } },
      }),
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
  const schedulesPage = useEventListPage({
    queryKey: ["events", "schedules"],
    enabled: tab === "schedules",
    fetchPage: (query) => apiGet("/api/events/schedules/list", { params: { query } }),
  });
  const unpublishedPage = useEventListPage({
    queryKey: ["events", "unpublished"],
    enabled: tab === "unpublished",
    fetchPage: (query) => apiGet("/api/events/unpublished/list", { params: { query } }),
  });

  const invalidateEvents = async () => {
    await queryClient.invalidateQueries({ queryKey: ["events"] });
  };

  const createSourceMutation = useMutation({
    mutationFn: (body: EventSourceCreateRequest) => apiPost("/api/events/sources/create", { body }),
    onSuccess: async (result) => {
      toast.success("Source created");
      if (result?.secret) setCreatedSource({ id: result.id, secret: result.secret });
      else setSourceDialogOpen(false);
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

  const { query: eventsQuery, items: events } = eventsPage;
  const { query: sourcesQuery, items: sources } = sourcesPage;
  const { query: subscriptionsQuery, items: subscriptions } = subscriptionsPage;
  const { query: deliveriesQuery, items: deliveries } = deliveriesPage;
  const { query: schedulesQuery, items: schedules } = schedulesPage;
  const { query: unpublishedQuery, items: unpublished } = unpublishedPage;

  const beginCreateSource = () => {
    setEditingSource(null);
    setCreatedSource(null);
    setSourceForm(createSourceForm());
    setSourceDialogOpen(true);
  };

  const beginEditSource = (source: EventSourceResponse) => {
    try {
      const form = sourceToForm(source);
      setEditingSource(source);
      setCreatedSource(null);
      setSourceForm(form);
      setSourceDialogOpen(true);
    } catch (error) {
      if (!(error instanceof InvalidSourceConfigurationError)) throw error;
      console.error("Invalid saved event source configuration", error);
      toast.error(`Source configuration is invalid: ${getApiErrorMessage(error)}`);
    }
  };

  const saveSource = (options: SourceFormOptions) => {
    try {
      if (editingSource) {
        updateSourceMutation.mutate({
          id: editingSource.id,
          version: editingSource.version,
          ...sourceFormToRequest(sourceForm, options),
        });
      } else {
        createSourceMutation.mutate(sourceFormToCreateRequest(sourceForm, options));
      }
    } catch (error) {
      toast.error(`Source configuration is invalid: ${getApiErrorMessage(error)}`);
    }
  };

  const beginCreateSubscription = () => {
    setEditingSubscription(null);
    setSubscriptionForm(createSubscriptionForm());
    setSubscriptionDialogOpen(true);
  };

  const openSubscription = (subscription: EventSubscriptionResponse) => {
    setEditingSubscription(subscription);
    setSubscriptionForm(subscriptionToForm(subscription));
    setSubscriptionDialogOpen(true);
  };

  const saveSubscription = () => {
    try {
      const request = subscriptionFormToRequest(subscriptionForm, editingSubscription);
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
                <Button
                  type="button"
                  onClick={() => {
                    setFilters({ ...draftFilters });
                    eventsPage.onPageIndexChange(1);
                  }}
                >
                  Apply filters
                </Button>
              </div>
            </CardContent>
          </Card>
          <div className="grid gap-4 xl:grid-cols-[minmax(0,1.15fr)_minmax(20rem,0.85fr)]">
            <Card className="min-h-0 overflow-hidden">
              <CardHeader className="border-b py-4">
                <CardTitle className="text-base">Event stream</CardTitle>
                <CardDescription>{eventsPage.total} events</CardDescription>
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
              <EventListPagination page={eventsPage} />
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
                      eventsPage.onPageIndexChange(1);
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
                        Event types: {source.eventTypes.join(", ")}
                      </p>
                      <p className="text-xs text-muted-foreground">
                        {source.lastRunAt
                          ? `Last run ${formatLocalDateTime(source.lastRunAt)}`
                          : "No runs yet"}
                        {source.nextRunAt ? ` · Next ${formatLocalDateTime(source.nextRunAt)}` : ""}
                      </p>
                      {source.lastDiscardedType ? (
                        <p className="text-xs text-muted-foreground">
                          Last discarded: {source.lastDiscardedType}
                          {source.lastDiscardedAt
                            ? ` · ${formatLocalDateTime(source.lastDiscardedAt)}`
                            : ""}
                        </p>
                      ) : null}
                      {source.lastError ? (
                        <p className="break-words text-xs text-destructive">{source.lastError}</p>
                      ) : null}
                    </div>
                    {getManagedLabel(source.managedBy, source.managedKey) ? (
                      <Badge variant="outline">Read only</Badge>
                    ) : (
                      <div className="flex flex-wrap gap-2">
                        {source.kind === EVENT_SOURCE_KIND.httpPoll ||
                        source.kind === EVENT_SOURCE_KIND.provider ? (
                          <Button
                            size="sm"
                            variant="outline"
                            onClick={() => sourceActionMutation.mutate({ action: "test", source })}
                          >
                            Test
                          </Button>
                        ) : null}
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => sourceActionMutation.mutate({ action: "enable", source })}
                        >
                          {source.enabled ? "Disable" : "Enable"}
                        </Button>
                        {source.kind !== EVENT_SOURCE_KIND.timer ? (
                          <Button
                            size="sm"
                            variant="outline"
                            onClick={() => beginEditSource(source)}
                          >
                            Edit
                          </Button>
                        ) : null}
                        {source.kind === EVENT_SOURCE_KIND.webhook ? (
                          <Button
                            size="sm"
                            variant="outline"
                            onClick={() =>
                              sourceActionMutation.mutate({ action: "rotate", source })
                            }
                          >
                            Rotate secret
                          </Button>
                        ) : null}
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
            <EventListPagination page={sourcesPage} />
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
                    {isUserManaged(subscription.managedBy) ? (
                      <div className="flex gap-2">
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => openSubscription(subscription)}
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
                    ) : (
                      <div className="flex items-center gap-2">
                        <Badge variant="outline">Read only</Badge>
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => openSubscription(subscription)}
                        >
                          View
                        </Button>
                      </div>
                    )}
                  </div>
                ))
              )}
            </CardContent>
            <EventListPagination page={subscriptionsPage} />
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
                  onChange={(event) => {
                    setSelectedEventId(event.target.value.trim() || null);
                    deliveriesPage.onPageIndexChange(1);
                  }}
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
            <EventListPagination page={deliveriesPage} />
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
            <EventListPagination page={schedulesPage} />
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
            <EventListPagination page={unpublishedPage} />
          </Card>
        </TabsContent>
      </Tabs>

      <SourceDialog
        open={sourceDialogOpen}
        editingSource={editingSource}
        form={sourceForm}
        createdSource={createdSource}
        pending={createSourceMutation.isPending || updateSourceMutation.isPending}
        onFormChange={setSourceForm}
        onOpenChange={setSourceDialogOpen}
        onSubmit={saveSource}
      />
      <SubscriptionDialog
        open={subscriptionDialogOpen}
        editingSubscription={editingSubscription}
        form={subscriptionForm}
        pending={saveSubscriptionMutation.isPending}
        onFormChange={setSubscriptionForm}
        onOpenChange={setSubscriptionDialogOpen}
        onSubmit={saveSubscription}
      />
    </section>
  );
}

function EventListPagination({ page }: { page: EventListPage<unknown> }) {
  return (
    <TablePagination
      pageIndex={page.pageIndex}
      pageSize={page.pageSize}
      total={page.total}
      isFetching={page.query.isFetching}
      pageSizeOptions={EVENT_PAGE_SIZE_OPTIONS}
      onPageIndexChange={page.onPageIndexChange}
      onPageSizeChange={page.onPageSizeChange}
    />
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
