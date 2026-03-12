import { Button } from "@/components/ui/button"

interface ProvidersHeaderProps {
  onRefresh: () => void
  isRefreshing: boolean
  onCreateClick: () => void
}

export function ProvidersHeader({
  onRefresh,
  isRefreshing,
  onCreateClick,
}: ProvidersHeaderProps) {
  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div className="min-w-0">
        <h1 className="truncate text-xl font-semibold">Providers</h1>
        <p className="text-sm text-muted-foreground">
          Manage model providers endpoints.
        </p>
      </div>

      <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
        <Button variant="outline" onClick={onRefresh} disabled={isRefreshing}>
          Refresh
        </Button>
        <Button onClick={onCreateClick}>Create provider</Button>
      </div>
    </div>
  )
}
