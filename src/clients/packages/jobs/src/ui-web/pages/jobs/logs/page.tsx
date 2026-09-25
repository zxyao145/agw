"use client";

import { useQuery } from "@agw/components/query";
import { ArrowLeft, MessageSquareText } from "lucide-react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import * as React from "react";

import { apiGet } from "@agw/api";
import { getApiErrorMessage } from "@agw/api";
import { buildChatHref } from "@agw/chat";
import { StaticTable } from "@agw/components";
import { Badge } from "@agw/components";
import { Button } from "@agw/components";
import { Empty } from "@agw/components";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@agw/components";
import { formatLocalDateTime } from "@agw/components";

type JobDto = {
  id: string;
  projectId: string;
  name: string;
};

type JobLogDto = {
  id: string;
  jobId: string;
  conversationId: string | null;
  startTime: string;
  endTime: string | null;
  success: boolean;
  attempt: number;
  errorMessage: string | null;
};

export default function JobLogsPage({
  chatBasePath,
}: {
  /** 宿主应用的 Chat 路由：Web 为 `/chat`，Desktop 为 `/desktop/chat`。Chat route of the host app. */
  chatBasePath: string;
}) {
  const searchParams = useSearchParams();
  const jobId = searchParams.get("jobId") ?? "";

  const jobQuery = useQuery({
    queryKey: ["job", jobId],
    enabled: Boolean(jobId),
    queryFn: async () =>
      (await apiGet("/api/jobs/{id}", {
        params: { path: { id: jobId } },
      })) as JobDto,
  });

  const logsQuery = useQuery({
    queryKey: ["job-logs", jobId],
    enabled: Boolean(jobId),
    queryFn: async () =>
      (await apiGet("/api/jobs/{id}/logs", {
        params: { path: { id: jobId } },
      })) as JobLogDto[],
  });

  const job = jobQuery.data;

  const getChatHref = React.useCallback(
    (log: JobLogDto) =>
      buildChatHref(chatBasePath, {
        projectId: job?.projectId ?? null,
        conversationId: log.conversationId,
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

        <Button variant="outline" asChild>
          <Link href="/jobs">
            <ArrowLeft className="mr-2 h-4 w-4" />
            Back to Jobs
          </Link>
        </Button>
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
              <TableHead>Status</TableHead>
              <TableHead>Attempt</TableHead>
              <TableHead>Job ID</TableHead>
              <TableHead>Time</TableHead>
              <TableHead>Error</TableHead>
              <TableHead className="text-right">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {logsQuery.data?.map((log) => (
              <TableRow key={log.id}>
                <TableCell>
                  <Badge variant={log.success ? "default" : "destructive"}>
                    {log.success ? "Succeeded" : "Failed"}
                  </Badge>
                </TableCell>
                <TableCell className="font-medium">#{log.attempt}</TableCell>
                <TableCell className="font-mono text-xs break-all">{log.jobId}</TableCell>
                <TableCell className="text-sm text-muted-foreground">
                  {formatLocalDateTime(log.startTime)}
                  {log.endTime ? ` -> ${formatLocalDateTime(log.endTime)}` : ""}
                </TableCell>
                <TableCell className="max-w-90 text-sm text-muted-foreground">
                  <div className="line-clamp-2 wrap-break-word">{log.errorMessage ?? "-"}</div>
                </TableCell>
                <TableCell className="text-right">
                  {job ? (
                    <Button type="button" variant="outline" size="sm" asChild>
                      <Link href={getChatHref(log)}>
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
    </div>
  );
}
