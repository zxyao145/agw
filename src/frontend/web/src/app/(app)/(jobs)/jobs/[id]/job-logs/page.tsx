"use client";

import Link from "next/link";
import * as React from "react";
import { useParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, MessageSquareText } from "lucide-react";

import { apiGet } from "@/api/client";
import { StaticTable } from "@/components/static-table";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Empty } from "@/components/ui/empty";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { getApiErrorMessage } from "@/api/utils";
import { isNonEmptyGuid } from "@/lib/guid";

type JobDto = {
  id: string;
  projectId: string;
  name: string;
};

type JobLogDto = {
  id: string;
  jobId: string;
  taskId: string;
  startTime: string;
  endTime: string | null;
  success: boolean;
  attempt: number;
  errorMessage: string | null;
};

function formatDateTime(value?: string | null): string {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(date);
}

export default function JobLogsPage() {
  const params = useParams<{ id: string }>();
  const jobId = params.id;

  const jobQuery = useQuery({
    queryKey: ["job", jobId],
    enabled: Boolean(jobId),
    queryFn: async () =>
      (await apiGet(
        "/api/jobs/{id}" as never,
        {
          params: { path: { id: jobId } },
        } as never,
      )) as JobDto,
  });

  const logsQuery = useQuery({
    queryKey: ["job-logs", jobId],
    enabled: Boolean(jobId),
    queryFn: async () =>
      (await apiGet(
        "/api/jobs/{id}/logs" as never,
        {
          params: { path: { id: jobId } },
        } as never,
      )) as JobLogDto[],
  });

  const job = jobQuery.data;

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
            {logsQuery.data?.map((log) => {
              const canOpenTask = job && isNonEmptyGuid(log.taskId);

              return (
                <TableRow key={log.id}>
                  <TableCell>
                    <Badge variant={log.success ? "default" : "destructive"}>
                      {log.success ? "Succeeded" : "Failed"}
                    </Badge>
                  </TableCell>
                  <TableCell className="font-medium">#{log.attempt}</TableCell>
                  <TableCell className="font-mono text-xs break-all">{log.jobId}</TableCell>
                  <TableCell className="text-sm text-muted-foreground">
                    {formatDateTime(log.startTime)}
                    {log.endTime ? ` -> ${formatDateTime(log.endTime)}` : ""}
                  </TableCell>
                  <TableCell className="max-w-90 text-sm text-muted-foreground">
                    <div className="line-clamp-2 wrap-break-word">{log.errorMessage ?? "-"}</div>
                  </TableCell>
                  <TableCell className="text-right">
                    {canOpenTask ? (
                      <Button type="button" variant="outline" size="sm" asChild>
                        <Link href={`/chat?projectId=${job.projectId}&taskId=${log.taskId}`}>
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
              );
            })}
          </TableBody>
        </StaticTable>
      )}
    </div>
  );
}
