"use client";

import { useQuery } from "@agw/components/query";
import { ArrowLeft, MessageSquareText } from "lucide-react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import * as React from "react";

import { apiGet } from "@agw/api";
import type { components } from "@agw/api";
import { getApiErrorMessage } from "@agw/api";
import { buildChatHref } from "@agw/chat";
import { StaticTable } from "@agw/components";
import { Button } from "@agw/components";
import { Empty } from "@agw/components";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@agw/components";
import { formatLocalDateTime } from "@agw/components";
import { JobRunPagination } from "../job-run-pagination";
import { JobRunResultBadge } from "../job-run-result";
import { useJobRunPage } from "../use-job-run-page";

type JobDto = components["schemas"]["JobResponse"];
type JobRunDto = components["schemas"]["JobRunResponse"];

export default function JobLogsPage({
  chatBasePath,
  jobId: requestedJobId,
  onBack,
  onOpenChat,
}: {
  /** 宿主应用的 Chat 路由：Web 为 `/chat`，Desktop 为 `/desktop/chat`。Chat route of the host app. */
  chatBasePath: string;
  jobId?: string;
  onBack?: () => void;
  onOpenChat?: () => void;
}) {
  const searchParams = useSearchParams();
  const jobId = requestedJobId ?? searchParams.get("jobId") ?? "";

  const jobQuery = useQuery({
    queryKey: ["job", jobId],
    enabled: Boolean(jobId),
    queryFn: async () =>
      (await apiGet("/api/jobs/get", {
        params: { query: { id: jobId } },
      })) as JobDto,
  });

  const jobRunPage = useJobRunPage(jobId || null, Boolean(jobId));
  const logsQuery = jobRunPage.logsQuery;

  const job = jobQuery.data;

  const getChatHref = React.useCallback(
    (log: JobRunDto) =>
      buildChatHref(chatBasePath, {
        projectId: job?.projectId ?? null,
        conversationId: log.conversationId ?? null,
      }),
    [chatBasePath, job],
  );

  return (
    <div className="w-full space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Job Logs</h1>
          <p className="text-sm text-muted-foreground">
            {job
              ? `All execution records for ${job.name}.`
              : "View all execution records for this job."}
          </p>
        </div>

        {onBack ? (
          <Button variant="outline" onClick={onBack}>
            <ArrowLeft className="mr-2 h-4 w-4" />
            Back to Jobs
          </Button>
        ) : (
          <Button variant="outline" asChild>
            <Link href="/jobs">
              <ArrowLeft className="mr-2 h-4 w-4" />
              Back to Jobs
            </Link>
          </Button>
        )}
      </div>

      {jobQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading job details...</div>
      ) : jobQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load job: {getApiErrorMessage(jobQuery.error)}
        </div>
      ) : null}

      {logsQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading logs...</div>
      ) : logsQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load logs: {getApiErrorMessage(logsQuery.error)}
        </div>
      ) : (
        <StaticTable isEmpty={logsQuery.data === undefined || logsQuery.data.length === 0}>
          <Empty>
            <div className="text-sm text-muted-foreground">
              No execution logs found for this job.
            </div>
          </Empty>
          <TableHeader>
            <TableRow>
              <TableHead>Result</TableHead>
              <TableHead>Attempt</TableHead>
              <TableHead>Delivery ID</TableHead>
              <TableHead>Time</TableHead>
              <TableHead>Error</TableHead>
              <TableHead className="text-right">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {jobRunPage.logs?.map((log) => (
              <TableRow key={log.id}>
                <TableCell>
                  <JobRunResultBadge result={log.result} />
                </TableCell>
                <TableCell className="font-medium">#{log.attempt}</TableCell>
                <TableCell className="font-mono text-xs break-all">{log.deliveryId}</TableCell>
                <TableCell className="text-sm text-muted-foreground">
                  {formatLocalDateTime(log.startedAt)}
                  {` -> ${formatLocalDateTime(log.endedAt)}`}
                </TableCell>
                <TableCell className="max-w-90 text-sm text-muted-foreground">
                  <div className="line-clamp-2 wrap-break-word">{log.error ?? "-"}</div>
                </TableCell>
                <TableCell className="text-right">
                  {job && log.conversationId ? (
                    <Button type="button" variant="outline" size="sm" asChild>
                      <Link href={getChatHref(log)} onClick={onOpenChat}>
                        <MessageSquareText className="mr-2 h-4 w-4" />
                        Go to Chat
                      </Link>
                    </Button>
                  ) : (
                    <Button type="button" variant="outline" size="sm" disabled>
                      <MessageSquareText className="mr-2 h-4 w-4" />
                      Go to Chat
                    </Button>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </StaticTable>
      )}
      {jobId ? (
        <JobRunPagination
          offset={jobRunPage.offset}
          hasNextPage={jobRunPage.hasNextPage}
          isLoading={logsQuery.isFetching}
          isError={logsQuery.isError}
          onPrevious={jobRunPage.goToPreviousPage}
          onNext={jobRunPage.goToNextPage}
        />
      ) : null}
    </div>
  );
}
