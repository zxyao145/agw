export default function Loading() {
  return (
    <div className="space-y-4">
      <div className="h-6 w-40 animate-pulse rounded bg-muted" />
      <div className="h-4 w-72 animate-pulse rounded bg-muted" />
      <div className="h-48 w-full animate-pulse rounded-lg border bg-muted/30" />
    </div>
  );
}
