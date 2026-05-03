"use client";

import { useQuery } from "@tanstack/react-query";

import { ApiError, apiGet } from "@/api/client";

type DashboardStatsResponse = {
  jobCount: number;
  projectCount: number;
  projectTaskCount: number;
  projectTaskRecordCount: number;
  agentCount: number;
  agentflowCount: number;
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
      label: "Job 数量",
      value: hasData ? stats?.jobCount : "—",
      color: "text-light",
    },
    {
      label: "Project 数量",
      value: hasData ? stats?.projectCount : "—",
      color: "text-sage",
    },
    {
      label: "Project Task 数量",
      value: hasData ? stats?.projectTaskCount : "—",
      color: "text-blue-400",
    },
    {
      label: "Project Task Record 数量",
      value: hasData ? stats?.projectTaskRecordCount : "—",
      color: "text-violet-400",
    },
    {
      label: "Agent 数量",
      value: hasData ? stats?.agentCount : "—",
      color: "text-amber-300",
    },
    {
      label: "Agentflow 数量",
      value: hasData ? stats?.agentflowCount : "—",
      color: "text-rose",
    },
  ];

  return (
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
      {loading
        ? Array.from({ length: cards.length }).map((_, i) => (
            <div key={i} className="animate-pulse rounded-xl border border-stone bg-charcoal p-4">
              <div className="h-3 w-28 rounded bg-stone" />
              <div className="mt-2 h-6 w-14 rounded bg-stone" />
            </div>
          ))
        : cards.map((c) => (
            <div key={c.label} className="rounded-xl border border-stone bg-charcoal p-4">
              <div className="text-xs text-dust">{c.label}</div>
              <div className={`mt-1 text-2xl font-bold ${c.color}`}>{c.value}</div>
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
    </div>
  );
}
