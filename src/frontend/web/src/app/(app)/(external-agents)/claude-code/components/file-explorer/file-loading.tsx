import { Loader2 } from "lucide-react";

const FileLoading = () => (
  <div className="flex items-center justify-center h-full">
    <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
  </div>
);

export default FileLoading;
