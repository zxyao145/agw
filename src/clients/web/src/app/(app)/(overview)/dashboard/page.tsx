"use client";

import { useQuery } from "@tanstack/react-query";

import { ApiError, apiGet } from "@/api/client";

import { TraceTable } from "./components/trace-table";

type DashboardStatsResponse = {
  jobCount: number;
  projectCount: number;
  projectContextCount: number;
  taskRecordCount: number;
  agentCount: number;
  agentflowCount: number;
  usageInputTokenCount: number;
  usageOutputTokenCount: number;
  usageTotalTokenCount: number;
};

function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length > 0) {
      return error.body;
    }

    if (error.body && typeof error.body === "object") {
      const candidateBody = error.body as {
        message?: unknown;
        error?: unknown;
        title?: unknown;
        detail?: unknown;
      };

      if (typeof candidateBody.message === "string" && candidateBody.message.trim().length > 0) {
        return candidateBody.message;
      }

      if (typeof candidateBody.error === "string" && candidateBody.error.trim().length > 0) {
        return candidateBody.error;
      }

      if (typeof candidateBody.detail === "string" && candidateBody.detail.trim().length > 0) {
        return candidateBody.detail;
      }

      if (typeof candidateBody.title === "string" && candidateBody.title.trim().length > 0) {
        return candidateBody.title;
      }
    }

    return `${error.status} ${error.statusText}`;
  }

  if (error instanceof Error) {
    return error.message;
  }

  return "Unknown error";
}

function formatStat(value: number | undefined, hasData: boolean): string {
  if (!hasData || value === undefined) {
    return "—";
  }

  return value.toLocaleString();
}

function SummaryCards({
  loading,
  stats,
  hasData,
}: {
  loading: boolean;
  stats?: DashboardStatsResponse;
  hasData: boolean;
}) {
  const cards = [
    {
      label: "Total Input Token",
      value: formatStat(stats?.usageInputTokenCount, hasData),
      color: "text-chart-1",
      bar: "bg-chart-1",
    },
    {
      label: "Total Output Token",
      value: formatStat(stats?.usageOutputTokenCount, hasData),
      color: "text-chart-3",
      bar: "bg-chart-3",
    },
    {
      label: "Total Token",
      value: formatStat(stats?.usageTotalTokenCount, hasData),
      color: "text-chart-2",
      bar: "bg-chart-2",
    },
    // {
    //   label: "Project",
    //   value: hasData ? stats?.projectCount : "—",
    //   color: "text-sage",
    // },
    // {
    //   label: "Context / Conversation / Session",
    //   value: hasData ? stats?.projectContextCount : "—",
    //   color: "text-blue-400",
    // },
    // {
    //   label: "Task Record",
    //   value: hasData ? stats?.taskRecordCount : "—",
    //   color: "text-violet-400",
    // },
    // {
    //   label: "Job",
    //   value: hasData ? stats?.jobCount : "—",
    //   color: "text-light",
    // },
    // {
    //   label: "Agent",
    //   value: hasData ? stats?.agentCount : "—",
    //   color: "text-amber-300",
    // },
    // {
    //   label: "Agentflow",
    //   value: hasData ? stats?.agentflowCount : "—",
    //   color: "text-rose",
    // },
  ];

  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
      {loading
        ? Array.from({ length: cards.length }).map((_, i) => (
            <div
              key={i}
              className="relative overflow-hidden rounded-xl border border-border bg-card p-5"
            >
              <div className="absolute inset-y-0 left-0 w-1 bg-muted" />
              <div className="animate-pulse pl-3">
                <div className="h-3 w-24 rounded bg-muted" />
                <div className="mt-3 h-7 w-32 rounded bg-muted" />
              </div>
            </div>
          ))
        : cards.map((c) => (
            <div
              key={c.label}
              className="relative overflow-hidden rounded-xl border border-border bg-card p-5 transition-colors hover:border-foreground/20"
            >
              <div className={`absolute inset-y-0 left-0 w-1 ${c.bar}`} />
              <div className="pl-3">
                <div className="text-[0.7rem] font-medium uppercase tracking-wider text-muted-foreground">
                  {c.label}
                </div>
                <div className={`mt-2 text-3xl font-semibold tabular-nums ${c.color}`}>
                  {c.value}
                </div>
              </div>
            </div>
          ))}
    </div>
  );
}

export default function Page() {
  const statsQuery = useQuery({
    queryKey: ["dashboard-stats"],
    queryFn: async () => (await apiGet("/api/dashboard/stats" as never)) as DashboardStatsResponse,
    refetchInterval: 10000,
  });

  return (
    <div className="mt-4 w-full space-y-4">
      <SummaryCards
        loading={statsQuery.isLoading}
        stats={statsQuery.data}
        hasData={statsQuery.data !== undefined}
      />

      {statsQuery.isError ? (
        <div className="rounded-xl border border-rose-500/40 bg-rose-500/10 p-4 text-sm text-rose-300">
          统计信息加载失败：{getApiErrorMessage(statsQuery.error)}
        </div>
      ) : null}

      <TraceTable />
    </div>
  );
}
