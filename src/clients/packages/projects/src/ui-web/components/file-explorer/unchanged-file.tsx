import FileViewer from "./file-viewer";
import { CommentSide, UnChangedFileProps } from "./types";

export default function UnChangedFile({
  diffContentData,
  selectedFile,
  diffScope,
  comments,
  setComments,
}: UnChangedFileProps): React.ReactNode {
  return (
    <div className="p-4">
      <div className="text-sm text-muted-foreground p-3 bg-muted/50 rounded mb-4">
        {diffContentData.message || "No changes detected"}
      </div>
      {diffContentData.originalContent && (
        <FileViewer
          content={diffContentData.originalContent}
          filePath={selectedFile}
          comments={comments}
          setComments={setComments}
          isDiffView={true}
          commentSide={CommentSide.Original}
          diffScope={diffScope}
        />
      )}
    </div>
  );
}
