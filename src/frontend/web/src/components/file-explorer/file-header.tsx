import { File } from "lucide-react";

const FileHeader = ({ file }: { file: string }) => {
  return (
    <div className="border-b px-4 py-2 bg-muted/30">
      <div className="flex items-center gap-2">
        <File className="h-4 w-4 text-muted-foreground" />
        <span className="text-sm font-medium truncate">{file.split(/[\\/]/).pop()}</span>
      </div>
      <p className="text-xs text-muted-foreground truncate mt-0.5">{file}</p>
    </div>
  );
};

export default FileHeader;
