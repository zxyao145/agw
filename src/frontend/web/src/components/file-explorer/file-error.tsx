import { Loader2 } from "lucide-react";

const FileError = ({ message }: { message: string | null }) => (
  <div className="p-4">
    <div className="text-sm text-destructive p-3 bg-destructive/10 rounded">
      Error loading file: {message}
    </div>
  </div>
);

export default FileError;
