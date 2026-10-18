import type { ReactNode } from "react";
import { File } from "lucide-react";

const FileHeader = ({ file, actions }: { file: string; actions?: ReactNode }) => {
  return (
    <div className="flex items-center gap-3 border-b px-4 py-2 bg-muted/30">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <File className="h-4 w-4 text-muted-foreground" />
          <span className="text-sm font-medium truncate">{file.split(/[\\/]/).pop()}</span>
        </div>
        <p className="text-xs text-muted-foreground truncate mt-0.5">{file}</p>
      </div>
      {actions ? <div className="flex shrink-0 items-center gap-1">{actions}</div> : null}
    </div>
  );
};

export default FileHeader;
