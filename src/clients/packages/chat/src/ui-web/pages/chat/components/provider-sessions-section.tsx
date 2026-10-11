"use client";

import * as React from "react";
import { Archive, ChevronRight, Loader2, RotateCw } from "lucide-react";
import { useQuery } from "@agw/components/query";
import { toast } from "sonner";

import { getApiErrorMessage } from "@agw/api";
import {
  Button,
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
  Label,
  Skeleton,
  cn,
  formatFriendlyLocalDateTime2,
} from "@agw/components";
import {
  archiveProviderSession,
  getProviderSessions,
  groupProviderSessions,
  type ProviderSessionGroup,
  type ProviderSessionRecord,
} from "@agw/projects";

export type ProviderSessionsSectionProps = {
  serverId: string;
  projectId: string;
  conversationId: string;
  /**
   * 对话正在执行、排队或等待用户操作；归档按钮在此期间不可用。
   * The conversation is running, queued or waiting for the user; archiving is unavailable meanwhile.
   */
  conversationRunning: boolean;
};

/**
 * Conversation Settings 中的 Engine Sessions 区域：展示 External Agent 的 provider session 绑定组，并归档当前生效的 session。
 * The Engine Sessions area of Conversation Settings: shows the External Agent provider session groups and archives the active session.
 */
export function ProviderSessionsSection({
  serverId,
  projectId,
  conversationId,
  conversationRunning,
}: ProviderSessionsSectionProps) {
  const target = React.useMemo(() => ({ projectId, conversationId }), [conversationId, projectId]);
  const sessionsQuery = useQuery({
    queryKey: ["provider-sessions", serverId, projectId, conversationId],
    queryFn: ({ signal }) => getProviderSessions(target, signal),
    staleTime: 0,
    refetchOnMount: "always",
  });
  const { refetch } = sessionsQuery;
  const [archivingId, setArchivingId] = React.useState<string | null>(null);

  // 执行结束后刷新：本次执行可能保存了新的 provider session。
  // Refresh when a run ends: the run may have saved a new provider session.
  const wasRunningRef = React.useRef(conversationRunning);
  React.useEffect(() => {
    if (wasRunningRef.current && !conversationRunning) void refetch();
    wasRunningRef.current = conversationRunning;
  }, [conversationRunning, refetch]);

  const groups = React.useMemo(
    () => (sessionsQuery.data ? groupProviderSessions(sessionsQuery.data) : []),
    [sessionsQuery.data],
  );

  const handleArchive = async (record: ProviderSessionRecord) => {
    setArchivingId(record.id);
    try {
      await archiveProviderSession({ ...target, bindingId: record.id });
      toast.success(`Archived the ${record.externalAgentName} engine session`);
      await refetch();
    } catch (error) {
      // 失败时保留当前展示内容，只提示服务端错误。
      // On failure the current content stays; only the server error is shown.
      toast.error(`Failed to archive the engine session: ${getApiErrorMessage(error)}`);
    } finally {
      setArchivingId(null);
    }
  };

  return (
    <section className="grid gap-2" aria-labelledby="chat-settings-provider-sessions">
      <div className="flex items-center justify-between gap-3">
        <Label id="chat-settings-provider-sessions">Engine Sessions</Label>
        {groups.length > 0 && (
          <span className="text-xs text-muted-foreground tabular-nums">
            {groups.length} {groups.length === 1 ? "agent" : "agents"}
          </span>
        )}
      </div>

      {conversationRunning && (
        <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
          <Loader2 className="h-3 w-3 animate-spin" aria-hidden />
          Archiving is available after the current run finishes.
        </p>
      )}

      {sessionsQuery.data === undefined && sessionsQuery.isFetching ? (
        <div className="grid gap-2 rounded-md border p-3" aria-busy="true">
          <Skeleton className="h-3.5 w-1/3" />
          <Skeleton className="h-3 w-full" />
          <Skeleton className="h-3 w-2/3" />
        </div>
      ) : sessionsQuery.data === undefined ? (
        <div className="flex items-center justify-between gap-3 rounded-lg border border-destructive/30 bg-destructive/5 px-4 py-3 text-sm">
          <span className="min-w-0 text-destructive">
            Couldn&apos;t load engine sessions: {getApiErrorMessage(sessionsQuery.error)}
          </span>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="shrink-0"
            onClick={() => void refetch()}
            disabled={sessionsQuery.isFetching}
          >
            <RotateCw className={cn("h-3.5 w-3.5", sessionsQuery.isFetching && "animate-spin")} />
            Retry
          </Button>
        </div>
      ) : groups.length === 0 ? (
        <div className="rounded-lg bg-muted/50 px-4 py-4 text-sm text-muted-foreground">
          No engine sessions yet. Claude Code, Codex and Pi Agents record their sessions here after
          they run in this conversation.
        </div>
      ) : (
        <div className="grid gap-2">
          {groups.map((group) => (
            <ProviderSessionGroupCard
              key={group.key}
              group={group}
              archiving={archivingId !== null && archivingId === group.active?.id}
              archiveDisabled={conversationRunning || archivingId !== null}
              onArchive={handleArchive}
            />
          ))}
        </div>
      )}
    </section>
  );
}

type ProviderSessionGroupCardProps = {
  group: ProviderSessionGroup;
  archiving: boolean;
  archiveDisabled: boolean;
  onArchive: (record: ProviderSessionRecord) => Promise<void>;
};

function ProviderSessionGroupCard({
  group,
  archiving,
  archiveDisabled,
  onArchive,
}: ProviderSessionGroupCardProps) {
  const { active } = group;

  return (
    <div className="rounded-md border text-xs">
      <div className="flex items-center justify-between gap-3 p-3">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <span className="text-sm font-medium">{group.externalAgentName}</span>
          <SessionStatus active={active !== null} />
        </div>
        {active && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="h-7 shrink-0 gap-1.5 px-2.5 text-xs shadow-none"
            disabled={archiveDisabled}
            aria-label={`Archive the ${group.externalAgentName} engine session`}
            onClick={() => void onArchive(active)}
          >
            {archiving ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden />
            ) : (
              <Archive className="h-3.5 w-3.5" aria-hidden />
            )}
            {archiving ? "Archiving…" : "Archive"}
          </Button>
        )}
      </div>

      {active && (
        <div className="space-y-1.5 border-t px-3 py-2.5">
          <div className="flex items-baseline justify-between gap-3 text-muted-foreground">
            <span>Current session</span>
            <span className="shrink-0">
              Started {formatFriendlyLocalDateTime2(active.createTime)}
            </span>
          </div>
          <div className="font-mono break-all text-foreground">{active.providerSessionId}</div>
        </div>
      )}

      {group.history.length > 0 && (
        <Collapsible>
          <CollapsibleTrigger className="group flex w-full cursor-pointer items-center gap-1.5 border-t px-3 py-2 text-left text-muted-foreground transition-colors hover:text-foreground">
            <ChevronRight
              className="h-3.5 w-3.5 transition-transform group-data-[state=open]:rotate-90"
              aria-hidden
            />
            History ({group.history.length})
          </CollapsibleTrigger>
          <CollapsibleContent>
            <ol className="divide-y border-t bg-muted/30">
              {group.history.map((record) => (
                <li key={record.id} className="flex items-baseline justify-between gap-3 px-3 py-2">
                  <span className="min-w-0 font-mono break-all">{record.providerSessionId}</span>
                  <span className="shrink-0 text-muted-foreground">
                    {formatFriendlyLocalDateTime2(record.createTime)}
                  </span>
                </li>
              ))}
            </ol>
          </CollapsibleContent>
        </Collapsible>
      )}
    </div>
  );
}

function SessionStatus({ active }: { active: boolean }) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full px-1.5 py-0.5 text-[11px] font-medium",
        active
          ? "bg-emerald-500/10 text-emerald-700 dark:text-emerald-300"
          : "bg-muted text-muted-foreground",
      )}
    >
      <span
        className={cn(
          "size-1.5 rounded-full",
          active ? "bg-emerald-500" : "bg-muted-foreground/60",
        )}
        aria-hidden
      />
      {active ? "Active" : "No active session"}
    </span>
  );
}
