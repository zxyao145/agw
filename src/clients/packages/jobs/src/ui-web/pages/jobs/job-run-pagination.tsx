import { Button } from "@agw/components";
import { JOB_RUN_PAGE_SIZE } from "./use-job-run-page";

export function JobRunPagination({
  offset,
  hasNextPage,
  isLoading,
  isError,
  onPrevious,
  onNext,
}: {
  offset: number;
  hasNextPage: boolean;
  isLoading: boolean;
  isError: boolean;
  onPrevious: () => void;
  onNext: () => void;
}) {
  return (
    <div className="flex flex-wrap items-center justify-end gap-2">
      {!isLoading && !isError && !hasNextPage ? (
        <span className="mr-auto text-sm text-muted-foreground">End of results</span>
      ) : null}
      <span className="mr-2 text-sm text-muted-foreground">
        Page {Math.floor(offset / JOB_RUN_PAGE_SIZE) + 1}
      </span>
      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={onPrevious}
        disabled={offset === 0 || isLoading}
      >
        Previous
      </Button>
      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={onNext}
        disabled={!hasNextPage || isLoading || isError}
      >
        Next
      </Button>
    </div>
  );
}
