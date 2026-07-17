export const PAGE_SIZE_OPTIONS = [10, 20, 50] as const;

export const DEFAULT_PAGE_SIZE = 20;

export type PagedResult<T> = {
  items: T[];
  total: number;
  pageIndex: number;
  pageSize: number;
};

export function getPaginationMeta(total: number, pageIndex: number, pageSize: number) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  return {
    start: total === 0 ? 0 : (pageIndex - 1) * pageSize + 1,
    end: total === 0 ? 0 : Math.min(pageIndex * pageSize, total),
    totalPages,
    canGoPrevious: pageIndex > 1,
    canGoNext: pageIndex < totalPages,
  };
}

export function getClampedPageIndex(total: number, pageIndex: number, pageSize: number): number {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  return Math.min(Math.max(1, pageIndex), totalPages);
}
