import * as React from "react";

import { apiGet } from "@agw/api";
import type { components } from "@agw/api";
import { useQuery } from "@agw/components/query";

type JobRunDto = components["schemas"]["JobRunResponse"];

export const JOB_RUN_PAGE_SIZE = 20;
const JOB_RUN_FETCH_SIZE = JOB_RUN_PAGE_SIZE + 1;

export function getJobRunQueryKey(jobId: string, offset: number) {
  return offset === 0
    ? (["job-logs", jobId] as const)
    : (["job-logs", jobId, offset, JOB_RUN_PAGE_SIZE] as const);
}

export function useJobRunPage(jobId: string | null, enabled: boolean) {
  const [page, setPage] = React.useState({ jobId, offset: 0 });
  const offset = page.jobId === jobId ? page.offset : 0;
  const queryJobId = jobId ?? "";

  React.useEffect(() => {
    setPage((current) => (current.jobId === jobId ? current : { jobId, offset: 0 }));
  }, [jobId]);

  const logsQuery = useQuery({
    queryKey: getJobRunQueryKey(queryJobId, offset),
    enabled: enabled && Boolean(jobId),
    queryFn: async () =>
      (await apiGet("/api/jobs/runs/list", {
        params: { query: { jobId: queryJobId, offset, limit: JOB_RUN_FETCH_SIZE } },
      })) as JobRunDto[],
  });

  return {
    logsQuery,
    logs: logsQuery.data?.slice(0, JOB_RUN_PAGE_SIZE),
    hasNextPage: (logsQuery.data?.length ?? 0) >= JOB_RUN_FETCH_SIZE,
    offset,
    goToPreviousPage: () => setPage({ jobId, offset: Math.max(0, offset - JOB_RUN_PAGE_SIZE) }),
    goToNextPage: () => setPage({ jobId, offset: offset + JOB_RUN_PAGE_SIZE }),
  };
}
