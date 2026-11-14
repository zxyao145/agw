"use client";

import { useState } from "react";
import { DEFAULT_PAGE_SIZE, getClampedPageIndex } from "@agw/components";
import { hashKey, keepPreviousData, useQuery, type UseQueryResult } from "@agw/components/query";

export const EVENT_PAGE_SIZE_OPTIONS = [10, 20, 50, 100] as const;

export type EventListPageRequest = { pageIndex: number; pageSize: number };

type EventListPageData<T> = { items: T[]; total: number | string };

export type EventListPage<T> = {
  query: UseQueryResult<EventListPageData<T>>;
  items: T[];
  total: number;
  pageIndex: number;
  pageSize: number;
  onPageIndexChange(pageIndex: number): void;
  onPageSizeChange(pageSize: number): void;
};

export function useEventListPage<T>({
  queryKey,
  enabled,
  fetchPage,
}: {
  /** 不含页码和每页条数的 query key。The query key without page values. */
  queryKey: readonly unknown[];
  enabled: boolean;
  fetchPage(request: EventListPageRequest): Promise<EventListPageData<T> | null>;
}): EventListPage<T> {
  const queryHash = hashKey(queryKey);
  const [pagination, setPagination] = useState({
    queryHash,
    pageIndex: 1,
    pageSize: DEFAULT_PAGE_SIZE,
  });
  const filtersChanged = pagination.queryHash !== queryHash;
  const pageIndex = filtersChanged ? 1 : pagination.pageIndex;
  const { pageSize } = pagination;
  const query = useQuery({
    queryKey: [...queryKey, pageIndex, pageSize],
    queryFn: async () => {
      const page = await fetchPage({ pageIndex, pageSize });
      if (page === null) {
        throw new Error("The event list response is missing page data.");
      }
      return page;
    },
    enabled,
    placeholderData: keepPreviousData,
  });
  const total = Number(query.data?.total ?? 0);

  if (filtersChanged) {
    setPagination({ queryHash, pageIndex, pageSize });
  } else if (enabled && query.isSuccess && !query.isPlaceholderData && !query.isFetching) {
    const clampedPageIndex = getClampedPageIndex(total, pageIndex, pageSize);
    if (clampedPageIndex !== pageIndex) {
      setPagination({ queryHash, pageIndex: clampedPageIndex, pageSize });
    }
  }

  return {
    query,
    items: query.data?.items ?? [],
    total,
    pageIndex,
    pageSize,
    onPageIndexChange: (nextPageIndex) =>
      setPagination({ queryHash, pageIndex: nextPageIndex, pageSize }),
    onPageSizeChange: (nextPageSize) =>
      setPagination({ queryHash, pageIndex: 1, pageSize: nextPageSize }),
  };
}
