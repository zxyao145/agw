"use client";

import { useState, type FormEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, RotateCcw, Search } from "lucide-react";

import { apiGet } from "@/api/client";
import type { components } from "@/api/openapi";
import { getApiErrorMessage } from "@/api/utils";
import { DateTimePicker } from "@/components/date-time-picker";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

import {
  buildTraceQuery,
  EMPTY_TRACE_FILTERS,
  extractTraceInputText,
  formatTraceStartTime,
  getNodeKindLabel,
  getPaginationMeta,
  getTraceStatusLabel,
  type TraceFilters,
} from "./trace-table-utils";

type Trace = components["schemas"]["AgentflowTraceDto"];

const STATUS_BADGE_VARIANT: Record<
  number,
  "destructive" | "outline" | "default" | "secondary" | null | undefined
> = {
  0: "default",
  1: "destructive",
  2: "outline",
  3: "destructive",
};

const COLUMN_COUNT = 10;

export function TraceTable() {
  const [draftFilters, setDraftFilters] = useState<TraceFilters>({ ...EMPTY_TRACE_FILTERS });
  const [appliedFilters, setAppliedFilters] = useState<TraceFilters>({ ...EMPTY_TRACE_FILTERS });
  const [pageIndex, setPageIndex] = useState(1);
  const [pageSize, setPageSize] = useState(20);

  const tracesQuery = useQuery({
    queryKey: ["dashboard-traces", appliedFilters, pageIndex, pageSize],
    queryFn: ({ signal }) =>
      apiGet("/api/traces", {
        params: { query: buildTraceQuery(appliedFilters, pageIndex, pageSize) },
        signal,
      }),
  });

  const traces: Trace[] = tracesQuery.data?.items ?? [];
  const total = Number(tracesQuery.data?.total ?? 0);
  const pagination = getPaginationMeta(total, pageIndex, pageSize);

  const submitFilters = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setAppliedFilters({ ...draftFilters });
    setPageIndex(1);
  };

  const resetFilters = () => {
    setDraftFilters({ ...EMPTY_TRACE_FILTERS });
    setAppliedFilters({ ...EMPTY_TRACE_FILTERS });
    setPageIndex(1);
  };

  return (
    <section className="overflow-hidden rounded-xl border border-stone bg-charcoal">
      <div className="border-b border-stone p-4 sm:p-5">
        <div className="mb-4">
          <h2 className="text-base font-semibold text-light">Execution traces</h2>
          <p className="mt-1 text-sm text-dust">
            Inspect agentflow node executions across projects and contexts.
          </p>
        </div>

        <form className="space-y-4" onSubmit={submitFilters}>
          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-5">
            <div className="space-y-1.5">
              <Label htmlFor="trace-project-id">Project ID</Label>
              <Input
                id="trace-project-id"
                value={draftFilters.projectId}
                onChange={(event) =>
                  setDraftFilters((current) => ({ ...current, projectId: event.target.value }))
                }
                placeholder="Project UUID"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="trace-context-id">Context ID</Label>
              <Input
                id="trace-context-id"
                value={draftFilters.contextId}
                onChange={(event) =>
                  setDraftFilters((current) => ({ ...current, contextId: event.target.value }))
                }
                placeholder="Context identifier"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="trace-agentflow-id">Agentflow ID</Label>
              <Input
                id="trace-agentflow-id"
                value={draftFilters.agentflowId}
                onChange={(event) =>
                  setDraftFilters((current) => ({ ...current, agentflowId: event.target.value }))
                }
                placeholder="Agentflow UUID"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="trace-from-utc-date">From</Label>
              <DateTimePicker
                id="trace-from-utc"
                clearable
                placeholder="Pick a date"
                value={draftFilters.fromUtc}
                onChange={(fromUtc) => setDraftFilters((current) => ({ ...current, fromUtc }))}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="trace-to-utc-date">To</Label>
              <DateTimePicker
                id="trace-to-utc"
                clearable
                placeholder="Pick a date"
                value={draftFilters.toUtc}
                onChange={(toUtc) => setDraftFilters((current) => ({ ...current, toUtc }))}
              />
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <Button type="submit" size="sm">
              <Search />
              Search
            </Button>
            <Button type="button" variant="outline" size="sm" onClick={resetFilters}>
              <RotateCcw />
              Reset
            </Button>
            {tracesQuery.isFetching && !tracesQuery.isPending ? (
              <span className="text-xs text-dust">Refreshing results…</span>
            ) : null}
          </div>
        </form>
      </div>

      <Table>
        <TableHeader className="bg-stone/40">
          <TableRow>
            <TableHead className="min-w-44">Start time</TableHead>
            <TableHead>Status</TableHead>
            <TableHead className="min-w-44">Node</TableHead>
            <TableHead className="text-right">Duration</TableHead>
            <TableHead className="min-w-66">Project</TableHead>
            <TableHead className="min-w-66">Context</TableHead>
            <TableHead className="min-w-66">Agentflow</TableHead>
            <TableHead className="min-w-64">Input</TableHead>
            <TableHead className="min-w-64">Error</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {tracesQuery.isPending ? (
            <TableRow>
              <TableCell colSpan={COLUMN_COUNT} className="h-28 text-center text-dust">
                Loading traces…
              </TableCell>
            </TableRow>
          ) : tracesQuery.isError ? (
            <TableRow>
              <TableCell colSpan={COLUMN_COUNT} className="h-28 text-center text-rose-300">
                Failed to load traces: {getApiErrorMessage(tracesQuery.error)}
              </TableCell>
            </TableRow>
          ) : traces.length === 0 ? (
            <TableRow>
              <TableCell colSpan={COLUMN_COUNT} className="h-28 text-center text-dust">
                No traces found for the selected filters.
              </TableCell>
            </TableRow>
          ) : (
            traces.map((trace) => {
              const inputText = extractTraceInputText(trace.input);

              return (
                <TableRow key={trace.id} className="align-top">
                  <TableCell className="whitespace-nowrap text-xs text-dust">
                    {formatTraceStartTime(trace.startTimeUtc)}
                  </TableCell>
                  <TableCell>
                    <Badge variant={STATUS_BADGE_VARIANT[trace.status]}>
                      {getTraceStatusLabel(trace.status)}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    <div className="font-medium text-light">{trace.nodeName || trace.nodeId}</div>
                    <div className="mt-0.5 text-xs text-dust">
                      {getNodeKindLabel(trace.nodeKind)}
                    </div>
                  </TableCell>
                  <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                    {Number(trace.durationMilliseconds).toLocaleString()} ms
                  </TableCell>
                  <TableCell>
                    <span
                      className="block max-w-66 truncate font-mono text-xs"
                      title={trace.projectId}
                    >
                      {trace.projectId}
                    </span>
                  </TableCell>
                  <TableCell>
                    <span
                      className="block max-w-66 truncate font-mono text-xs"
                      title={trace.contextId}
                    >
                      {trace.contextId}
                    </span>
                  </TableCell>
                  <TableCell>
                    <span
                      className="block max-w-66 truncate font-mono text-xs"
                      title={trace.agentflowId}
                    >
                      {trace.agentflowId}
                    </span>
                  </TableCell>
                  <TableCell>
                    <Tooltip>
                      <TooltipTrigger asChild>
                        <span className="block max-w-64 truncate text-xs text-dust" tabIndex={0}>
                          {inputText}
                        </span>
                      </TooltipTrigger>
                      <TooltipContent
                        side="top"
                        className="max-h-80 max-w-[min(40rem,calc(100vw-2rem))] overflow-y-auto whitespace-pre-wrap break-words text-left"
                      >
                        {inputText}
                      </TooltipContent>
                    </Tooltip>
                  </TableCell>
                  <TableCell>
                    {trace.error ? (
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <span
                            className="block max-w-64 truncate text-xs text-rose-300"
                            tabIndex={0}
                          >
                            {trace.error}
                          </span>
                        </TooltipTrigger>
                        <TooltipContent
                          side="top"
                          className="max-h-80 max-w-[min(40rem,calc(100vw-2rem))] overflow-y-auto whitespace-pre-wrap break-words text-left"
                        >
                          {trace.error}
                        </TooltipContent>
                      </Tooltip>
                    ) : (
                      <span className="text-xs text-rose-300">—</span>
                    )}
                  </TableCell>
                </TableRow>
              );
            })
          )}
        </TableBody>
      </Table>

      <div className="flex flex-col gap-3 border-t border-stone px-4 py-3 text-sm text-dust sm:flex-row sm:items-center sm:justify-between">
        <span>
          Showing {pagination.start}–{pagination.end} of {total.toLocaleString()}
        </span>
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex items-center gap-2">
            <span>Rows</span>
            <Select
              value={String(pageSize)}
              onValueChange={(value) => {
                setPageSize(Number(value));
                setPageIndex(1);
              }}
            >
              <SelectTrigger size="sm" className="w-20">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {[10, 20, 50, 100].map((size) => (
                  <SelectItem key={size} value={String(size)}>
                    {size}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <span>
            Page {pageIndex} of {pagination.totalPages}
          </span>
          <div className="flex items-center gap-1">
            <Button
              type="button"
              variant="outline"
              size="icon-sm"
              aria-label="Previous page"
              disabled={!pagination.canGoPrevious || tracesQuery.isFetching}
              onClick={() => setPageIndex((current) => Math.max(1, current - 1))}
            >
              <ChevronLeft />
            </Button>
            <Button
              type="button"
              variant="outline"
              size="icon-sm"
              aria-label="Next page"
              disabled={!pagination.canGoNext || tracesQuery.isFetching}
              onClick={() => setPageIndex((current) => current + 1)}
            >
              <ChevronRight />
            </Button>
          </div>
        </div>
      </div>
    </section>
  );
}
