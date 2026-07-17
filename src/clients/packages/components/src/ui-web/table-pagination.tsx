"use client";

import type { ReactNode } from "react";

import { getPaginationMeta, PAGE_SIZE_OPTIONS } from "../lib/pagination";
import {
  Pagination,
  PaginationContent,
  PaginationItem,
  PaginationNext,
  PaginationPrevious,
} from "./shadcn/pagination";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "./shadcn/select";

type TablePaginationProps = {
  pageIndex: number;
  pageSize: number;
  total: number;
  isFetching: boolean;
  onPageIndexChange: (pageIndex: number) => void;
  onPageSizeChange: (pageSize: number) => void;
};

function TablePagination({
  pageIndex,
  pageSize,
  total,
  isFetching,
  onPageIndexChange,
  onPageSizeChange,
}: TablePaginationProps) {
  if (total === 0) {
    return null;
  }

  const pagination = getPaginationMeta(total, pageIndex, pageSize);

  return (
    <div className="flex flex-col gap-3 border-t bg-card px-2 py-1 text-sm text-muted-foreground sm:flex-row sm:items-center sm:justify-between">
      <span>
        Showing {pagination.start}–{pagination.end} of {total.toLocaleString()}
      </span>
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2">
          <span>Rows</span>
          <Select
            value={String(pageSize)}
            onValueChange={(value) => onPageSizeChange(Number(value))}
          >
            <SelectTrigger
              size="sm"
              className="w-18 data-[size=sm]:h-7 rounded"
              aria-label="Rows per page"
            >
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {PAGE_SIZE_OPTIONS.map((size) => (
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
        <Pagination className="mx-0 w-auto justify-start">
          <PaginationContent>
            <PaginationItem>
              <PaginationPrevious
                type="button"
                aria-label="Previous page"
                disabled={!pagination.canGoPrevious || isFetching}
                onClick={() => onPageIndexChange(Math.max(1, pageIndex - 1))}
              />
            </PaginationItem>
            <PaginationItem>
              <PaginationNext
                type="button"
                aria-label="Next page"
                disabled={!pagination.canGoNext || isFetching}
                onClick={() => onPageIndexChange(pageIndex + 1)}
              />
            </PaginationItem>
          </PaginationContent>
        </Pagination>
      </div>
    </div>
  );
}

export function PaginatedTable({
  children,
  ...paginationProps
}: TablePaginationProps & { children: ReactNode }) {
  return (
    <div className="overflow-hidden rounded-md border bg-card">
      {children}
      <TablePagination {...paginationProps} />
    </div>
  );
}
