"use client";

import * as React from "react";
import Link from "next/link";
import { Bell, Check, ExternalLink, RotateCcw } from "lucide-react";
import { toast } from "sonner";

import {
  apiGet,
  apiPost,
  getApiErrorMessage,
  type NotificationAnswerRequest,
  type NotificationDetail,
  type NotificationListItem,
} from "@agw/api";
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Label,
  Textarea,
  formatLocalDateTime,
} from "@agw/components";
import { useMutation, useQuery, useQueryClient } from "@agw/components/query";

import { mergeNotificationItem, parseNotificationStreamItem } from "./notification-state";

type DesktopNotificationFrame = {
  serverId: string;
  notification: {
    id: string;
    createdAt: string;
    kind: number | string;
    state: number | string;
    title: string;
    text: string;
    link: string | null;
    readAt: string | null;
    changedAt: string;
    version: number;
  };
};

export type NotificationBridge = {
  onNotification(listener: (frame: DesktopNotificationFrame) => void): () => void;
};

const allNotificationsKey = ["notifications", false] as const;
const unreadNotificationsKey = ["notifications", true] as const;

function stateLabel(state: number): string {
  return ["Open", "Answered", "Resolved", "Closed"][state] ?? `State ${state}`;
}

function dispatchLabel(state: number): string {
  return (
    ["Pending", "Sending", "Sent", "Update pending", "Failed", "Parked", "Unsupported"][state] ??
    `State ${state}`
  );
}

function isQuestion(item: NotificationListItem | NotificationDetail): boolean {
  return item.kind === 1;
}

function internalLink(link: string | null): string | null {
  return link?.startsWith("/") && !link.startsWith("//") ? link : null;
}

function useNotificationsLiveUpdates(
  enabled: boolean,
  bridge: NotificationBridge | null | undefined,
  serverId: string | undefined,
) {
  const queryClient = useQueryClient();
  React.useEffect(() => {
    if (!enabled) return;
    const applyFrame = (incoming: NotificationListItem) => {
      for (const key of [allNotificationsKey, unreadNotificationsKey]) {
        const current = queryClient.getQueryData<NotificationListItem[]>(key);
        if (!current) continue;
        const merged = mergeNotificationItem(current, incoming);
        queryClient.setQueryData(
          key,
          key[1] ? merged.filter((item) => item.readAt === null) : merged,
        );
      }
      void queryClient.invalidateQueries({ queryKey: ["notification"] });
    };

    if (bridge) {
      return bridge.onNotification((frame) => {
        if (serverId && frame.serverId !== serverId) return;
        applyFrame(parseNotificationStreamItem(frame.notification));
      });
    }

    if (typeof window === "undefined") return;
    const stream = new EventSource("/api/notifications/stream");
    const onNotification = (event: Event) => {
      const message = event as MessageEvent<string>;
      applyFrame(parseNotificationStreamItem(JSON.parse(message.data) as unknown));
    };
    stream.addEventListener("notification", onNotification);
    return () => {
      stream.removeEventListener("notification", onNotification);
      stream.close();
    };
  }, [bridge, enabled, queryClient, serverId]);
}

export function NotificationsLiveSync({
  desktopBridge,
  serverId,
}: {
  desktopBridge?: NotificationBridge | null;
  serverId?: string;
} = {}) {
  useNotificationsLiveUpdates(true, desktopBridge, serverId);
  return null;
}

export function useNotificationsUnreadCount(): number {
  const unreadQuery = useQuery({
    queryKey: unreadNotificationsKey,
    queryFn: () => apiGet("/api/notifications/list", { params: { query: { unreadOnly: true } } }),
  });
  return unreadQuery.data?.length ?? 0;
}

export function NotificationsUnreadCount() {
  const count = useNotificationsUnreadCount();
  if (count === 0) return null;
  return (
    <span
      className="inline-flex min-w-5 items-center justify-center rounded-full bg-primary px-1.5 py-0.5 text-[10px] font-semibold leading-none text-primary-foreground"
      aria-label={`${count} unread notifications`}
    >
      {count > 99 ? "99+" : count}
    </span>
  );
}

export function NotificationsPage({
  desktopBridge,
  serverId,
  initialNotificationId,
  liveUpdatesEnabled = true,
}: {
  desktopBridge?: NotificationBridge | null;
  serverId?: string;
  initialNotificationId?: string | null;
  liveUpdatesEnabled?: boolean;
}) {
  const queryClient = useQueryClient();
  const [selectedId, setSelectedId] = React.useState(initialNotificationId ?? null);
  const [selectedOption, setSelectedOption] = React.useState<string>("");
  const [answerText, setAnswerText] = React.useState("");
  const notificationsQuery = useQuery({
    queryKey: allNotificationsKey,
    queryFn: () => apiGet("/api/notifications/list", { params: { query: { unreadOnly: false } } }),
  });
  const unreadQuery = useQuery({
    queryKey: unreadNotificationsKey,
    queryFn: () => apiGet("/api/notifications/list", { params: { query: { unreadOnly: true } } }),
  });
  const detailQuery = useQuery({
    queryKey: ["notification", selectedId],
    enabled: Boolean(selectedId),
    queryFn: () => apiGet("/api/notifications/get", { params: { query: { id: selectedId! } } }),
  });
  const notification = detailQuery.data;
  const question = notification?.question;
  useNotificationsLiveUpdates(liveUpdatesEnabled, desktopBridge, serverId);

  React.useEffect(() => {
    setSelectedId(initialNotificationId ?? null);
  }, [initialNotificationId]);

  React.useEffect(() => {
    setSelectedOption(
      question?.recommend === null || question?.recommend === undefined
        ? ""
        : String(question.recommend),
    );
    setAnswerText("");
  }, [notification?.id, notification?.version, question?.recommend]);

  const readMutation = useMutation({
    mutationFn: (notificationId: string) =>
      apiPost("/api/notifications/read", { body: { notificationId } }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["notifications"] });
      await queryClient.invalidateQueries({ queryKey: ["notification", selectedId] });
    },
    onError: (error) =>
      toast.error(`Unable to mark notification as read: ${getApiErrorMessage(error)}`),
  });
  const answerMutation = useMutation({
    mutationFn: (request: NotificationAnswerRequest) =>
      apiPost("/api/notifications/answer", { body: request }),
    onSuccess: async () => {
      toast.success("Answer submitted");
      await queryClient.invalidateQueries({ queryKey: ["notifications"] });
      await queryClient.invalidateQueries({ queryKey: ["notification", selectedId] });
    },
    onError: async (error) => {
      toast.error(`Unable to submit answer: ${getApiErrorMessage(error)}`);
      await queryClient.invalidateQueries({ queryKey: ["notification", selectedId] });
    },
  });
  const retryMutation = useMutation({
    mutationFn: (dispatchId: string) =>
      apiPost("/api/notifications/dispatches/retry", { body: { dispatchId } }),
    onSuccess: async () => {
      toast.success("Notification delivery queued");
      await queryClient.invalidateQueries({ queryKey: ["notification", selectedId] });
    },
    onError: (error) =>
      toast.error(`Unable to retry notification delivery: ${getApiErrorMessage(error)}`),
  });

  const items = notificationsQuery.data ?? [];
  const unreadCount = unreadQuery.data?.length ?? 0;

  const selectNotification = (item: NotificationListItem) => {
    setSelectedId(item.id);
    if (item.readAt === null) readMutation.mutate(item.id);
  };

  const submitAnswer = () => {
    if (!notification || !question) return;
    const option = selectedOption === "" ? null : Number(selectedOption);
    const text = answerText.trim() || null;
    if (option === null && !text) return;
    answerMutation.mutate({ notificationId: notification.id, option, text });
  };

  return (
    <section className="flex w-full max-w-7xl flex-col gap-5 p-4 sm:p-6">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div className="space-y-1">
          <div className="flex items-center gap-2">
            <Bell className="size-5 text-primary" />
            <h1 className="text-xl font-semibold">Notifications</h1>
            <Badge variant="secondary">{unreadCount} unread</Badge>
          </div>
          <p className="text-sm text-muted-foreground">
            Updates and questions addressed to your account.
          </p>
        </div>
        <Button
          type="button"
          size="sm"
          variant="outline"
          disabled={notificationsQuery.isFetching}
          onClick={() => void queryClient.invalidateQueries({ queryKey: ["notifications"] })}
        >
          <RotateCcw className="size-4" />
          Refresh
        </Button>
      </header>

      <div className="grid min-h-[32rem] gap-4 xl:grid-cols-[minmax(17rem,0.8fr)_minmax(0,1.7fr)]">
        <Card className="min-h-0 overflow-hidden">
          <CardHeader className="border-b py-4">
            <CardTitle className="text-base">Inbox</CardTitle>
            <CardDescription>{items.length} notifications</CardDescription>
          </CardHeader>
          <CardContent className="max-h-[38rem] overflow-y-auto p-0">
            {notificationsQuery.isPending ? (
              <p className="p-5 text-sm text-muted-foreground" role="status">
                Loading notifications…
              </p>
            ) : notificationsQuery.isError ? (
              <p className="p-5 text-sm text-destructive" role="alert">
                {getApiErrorMessage(notificationsQuery.error)}
              </p>
            ) : items.length === 0 ? (
              <p className="p-5 text-sm text-muted-foreground">You’re all caught up.</p>
            ) : (
              <ul className="divide-y">
                {items.map((item) => (
                  <li key={item.id}>
                    <button
                      type="button"
                      className={`w-full px-4 py-3 text-left outline-none transition-colors hover:bg-muted/60 focus-visible:ring-2 focus-visible:ring-ring ${selectedId === item.id ? "bg-muted/70" : ""}`}
                      aria-current={selectedId === item.id ? "true" : undefined}
                      onClick={() => selectNotification(item)}
                    >
                      <div className="flex items-start justify-between gap-3">
                        <span className="flex min-w-0 items-center gap-2">
                          {item.readAt === null ? (
                            <span
                              className="size-2 shrink-0 rounded-full bg-primary"
                              aria-label="Unread"
                            />
                          ) : null}
                          <span className="truncate text-sm font-medium">{item.title}</span>
                        </span>
                        <span className="shrink-0 text-[11px] text-muted-foreground">
                          {formatLocalDateTime(item.changedAt)}
                        </span>
                      </div>
                      <p className="mt-1 line-clamp-2 pl-4 text-xs text-muted-foreground">
                        {item.text}
                      </p>
                      <div className="mt-2 flex items-center gap-2 pl-4">
                        <Badge variant={isQuestion(item) ? "default" : "secondary"}>
                          {isQuestion(item) ? "Question" : "Notice"}
                        </Badge>
                        <span className="text-[11px] text-muted-foreground">
                          {stateLabel(item.state)}
                        </span>
                      </div>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card className="min-h-0 overflow-hidden">
          {!selectedId ? (
            <div className="grid min-h-[24rem] place-items-center p-8 text-center">
              <div className="max-w-sm space-y-2">
                <Bell className="mx-auto size-8 text-muted-foreground" />
                <h2 className="font-medium">Select a notification</h2>
                <p className="text-sm text-muted-foreground">
                  Details, answers, and channel delivery progress will appear here.
                </p>
              </div>
            </div>
          ) : detailQuery.isPending ? (
            <p className="p-6 text-sm text-muted-foreground" role="status">
              Loading details…
            </p>
          ) : detailQuery.isError ? (
            <p className="p-6 text-sm text-destructive" role="alert">
              {getApiErrorMessage(detailQuery.error)}
            </p>
          ) : notification ? (
            <div className="max-h-[42rem] overflow-y-auto">
              <CardHeader className="border-b">
                <div className="flex flex-wrap items-center gap-2">
                  <Badge variant={isQuestion(notification) ? "default" : "secondary"}>
                    {isQuestion(notification) ? "Question" : "Notice"}
                  </Badge>
                  <Badge variant="outline">{stateLabel(notification.state)}</Badge>
                  {notification.readAt ? (
                    <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
                      <Check className="size-3" /> Read
                    </span>
                  ) : null}
                </div>
                <CardTitle className="break-words text-lg">{notification.title}</CardTitle>
                <CardDescription>
                  Updated {formatLocalDateTime(notification.changedAt)}
                </CardDescription>
              </CardHeader>
              <CardContent className="space-y-6 p-5">
                <p className="whitespace-pre-wrap break-words text-sm leading-6">
                  {notification.text}
                </p>
                {internalLink(notification.link) ? (
                  <Button asChild variant="outline" size="sm">
                    <Link href={internalLink(notification.link)!}>
                      <ExternalLink className="size-4" />
                      Open related item
                    </Link>
                  </Button>
                ) : null}

                {isQuestion(notification) && question ? (
                  <section
                    className="space-y-3 rounded-lg border bg-muted/20 p-4"
                    aria-labelledby="notification-answer-title"
                  >
                    <div>
                      <h2 id="notification-answer-title" className="text-sm font-semibold">
                        Answer
                      </h2>
                      <p className="text-xs text-muted-foreground">
                        Your answer is shared with the sender.
                      </p>
                    </div>
                    {question.answeredAt ? (
                      <div className="space-y-2 text-sm">
                        {question.answerOption !== null ? (
                          <p>
                            <span className="font-medium">Choice:</span>{" "}
                            {question.options[question.answerOption]?.label ??
                              `Option ${question.answerOption + 1}`}
                          </p>
                        ) : null}
                        {question.answerText ? (
                          <p className="whitespace-pre-wrap">
                            <span className="font-medium">Details:</span> {question.answerText}
                          </p>
                        ) : null}
                        <p className="text-xs text-muted-foreground">
                          Answered {formatLocalDateTime(question.answeredAt)}
                          {question.answeredVia ? ` via ${question.answeredVia}` : ""}
                        </p>
                      </div>
                    ) : notification.state === 0 ? (
                      <div className="space-y-4">
                        {question.options.length > 0 ? (
                          <fieldset className="space-y-2">
                            <legend className="text-sm font-medium">Choose an option</legend>
                            {question.options.map((option, index) => (
                              <label
                                key={`${index}:${option.value}`}
                                className="flex cursor-pointer items-center gap-2 rounded-md border bg-background px-3 py-2 text-sm focus-within:ring-2 focus-within:ring-ring"
                              >
                                <input
                                  type="radio"
                                  name="notification-answer-option"
                                  value={String(index)}
                                  checked={selectedOption === String(index)}
                                  onChange={(event) => setSelectedOption(event.target.value)}
                                  className="accent-primary"
                                />
                                <span>{option.label}</span>
                              </label>
                            ))}
                          </fieldset>
                        ) : null}
                        {question.allowText ? (
                          <div className="space-y-2">
                            <Label htmlFor="notification-answer-text">Additional details</Label>
                            <Textarea
                              id="notification-answer-text"
                              value={answerText}
                              rows={3}
                              onChange={(event) => setAnswerText(event.target.value)}
                            />
                          </div>
                        ) : null}
                        <Button
                          type="button"
                          onClick={submitAnswer}
                          disabled={
                            answerMutation.isPending ||
                            (selectedOption === "" && !answerText.trim())
                          }
                        >
                          {answerMutation.isPending ? "Submitting…" : "Submit answer"}
                        </Button>
                      </div>
                    ) : (
                      <p className="text-sm text-muted-foreground">
                        This question is no longer open.
                      </p>
                    )}
                  </section>
                ) : null}

                <section className="space-y-3" aria-labelledby="notification-delivery-title">
                  <div>
                    <h2 id="notification-delivery-title" className="text-sm font-semibold">
                      Delivery
                    </h2>
                    <p className="text-xs text-muted-foreground">
                      Channel status for the current notification version.
                    </p>
                  </div>
                  {notification.dispatches.length === 0 ? (
                    <p className="text-sm text-muted-foreground">No channel delivery records.</p>
                  ) : (
                    <ul className="space-y-2">
                      {notification.dispatches.map((dispatch) => (
                        <li key={dispatch.id} className="rounded-md border px-3 py-2">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <div className="flex min-w-0 items-center gap-2">
                              <span className="truncate text-sm font-medium">
                                {dispatch.channelKey}
                              </span>
                              <Badge
                                variant={
                                  dispatch.state === 2
                                    ? "secondary"
                                    : dispatch.state === 5
                                      ? "destructive"
                                      : "outline"
                                }
                              >
                                {dispatchLabel(dispatch.state)}
                              </Badge>
                            </div>
                            {dispatch.state === 5 ? (
                              <Button
                                type="button"
                                size="sm"
                                variant="outline"
                                onClick={() => retryMutation.mutate(dispatch.id)}
                                disabled={retryMutation.isPending}
                              >
                                <RotateCcw className="size-3.5" />
                                Retry
                              </Button>
                            ) : null}
                          </div>
                          <p className="mt-1 text-xs text-muted-foreground">
                            Version {dispatch.sentVersion} sent · {dispatch.attempt} attempts
                          </p>
                          {dispatch.lastError ? (
                            <p className="mt-2 break-words text-xs text-destructive">
                              {dispatch.lastError}
                            </p>
                          ) : null}
                        </li>
                      ))}
                    </ul>
                  )}
                </section>
              </CardContent>
            </div>
          ) : null}
        </Card>
      </div>
    </section>
  );
}
