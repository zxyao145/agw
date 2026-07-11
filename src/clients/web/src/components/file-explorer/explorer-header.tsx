import { Loader2, RotateCw } from "lucide-react";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";

export default function ExplorerHeader({
  isLoading,
  loadRootDirectory,
  rootDirectory,
  onlyDiff,
  onOnlyDiffChange,
}: {
  isLoading: boolean;
  loadRootDirectory: () => Promise<void>;
  rootDirectory: string;
  onlyDiff?: boolean;
  onOnlyDiffChange?: (value: boolean) => void;
}) {
  return (
    <div className="border-b px-3 py-2 bg-muted/50">
      <div className="flex items-center justify-between gap-2">
        <h3 className="text-sm font-medium">File Explorer</h3>
        <div className="flex items-center gap-3">
          {onOnlyDiffChange && (
            <div className="flex items-center gap-2">
              <Switch id="diff-mode" checked={!!onlyDiff} onCheckedChange={onOnlyDiffChange} />
              <Label htmlFor="diff-mode" className="text-sm cursor-pointer">
                Diff
              </Label>
            </div>
          )}
          {isLoading ? (
            <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
          ) : (
            <RotateCw onClick={loadRootDirectory} className="h-4 w-4 cursor-pointer" />
          )}
        </div>
      </div>
      {rootDirectory && (
        <p className="text-xs text-muted-foreground truncate mt-1">{rootDirectory}</p>
      )}
    </div>
  );
}
