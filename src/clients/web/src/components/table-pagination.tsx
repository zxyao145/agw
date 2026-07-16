"use client";

import { ChevronLeft, ChevronRight } from "lucide-react";

import { getPaginationMeta, PAGE_SIZE_OPTIONS } from "@/lib/pagination";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

type TablePaginationProps = {
  pageIndex: number;
  pageSize: number;
  total: number;
  isFetching: boolean;
  onPageIndexChange: (pageIndex: number) => void;
  onPageSizeChange: (pageSize: number) => void;
};

export function TablePagination({
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
    <div className="flex flex-col gap-3 rounded-md border px-4 py-3 text-sm text-muted-foreground sm:flex-row sm:items-center sm:justify-between">
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
            <SelectTrigger size="sm" className="w-20" aria-label="Rows per page">
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
        <div className="flex items-center gap-1">
          <Button
            type="button"
            variant="outline"
            size="icon-sm"
            aria-label="Previous page"
            disabled={!pagination.canGoPrevious || isFetching}
            onClick={() => onPageIndexChange(Math.max(1, pageIndex - 1))}
          >
            <ChevronLeft />
          </Button>
          <Button
            type="button"
            variant="outline"
            size="icon-sm"
            aria-label="Next page"
            disabled={!pagination.canGoNext || isFetching}
            onClick={() => onPageIndexChange(pageIndex + 1)}
          >
            <ChevronRight />
          </Button>
        </div>
      </div>
    </div>
  );
}
