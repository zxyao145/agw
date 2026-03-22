import { FileText } from "lucide-react";

export default function NoSelectedFile(): React.ReactNode {
  return (
    <div className="flex items-center justify-center h-full text-muted-foreground">
      <div className="text-center">
        <FileText className="h-12 w-12 mx-auto mb-3 opacity-50" />
        <p className="text-sm">Select a file to view its contents</p>
      </div>
    </div>
  );
}
