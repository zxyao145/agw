import { Loader2, RotateCw } from "lucide-react";


export default function ExplorerHeader({
  isLoading,
  loadRootDirectory,
  rootDirectory,
}: {
  isLoading: boolean;
  loadRootDirectory: () => Promise<void>;
  rootDirectory: string;
}) {
  return (
    <div className="border-b px-3 py-2 bg-muted/50">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-medium">File Explorer</h3>
        {isLoading ? (
          <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
        ) : (
          <RotateCw
            onClick={loadRootDirectory}
            className="h-4 w-4 cursor-pointer"
          />
        )}
      </div>
      {rootDirectory && (
        <p className="text-xs text-muted-foreground truncate mt-1">
          {rootDirectory}
        </p>
      )}
    </div>
  );
}
