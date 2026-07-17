import type { GitDiffResponse } from "../../../services/files";
import { DiffViewer } from "./diff-viewer";
import FileError from "./file-error";
import FileHeader from "./file-header";
import FileLoading from "./file-loading";
import FileViewer from "./file-viewer";
import NoSelectedFile from "./no-selected-file";
import { LineComment } from "./types";
import UnChangedFile from "./unchanged-file";

interface FileContentProps {
  selectedFile: string | null;
  isLoadingContent: boolean;
  contentError: string | null;
  onlyDiff: boolean;
  diffContentData: GitDiffResponse | null;
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
  fileContent: string;
}

export default function FileContent({
  selectedFile,
  isLoadingContent,
  contentError,
  onlyDiff,
  diffContentData,
  comments,
  setComments,
  fileContent,
}: FileContentProps) {
  return (
    <div className="flex flex-col h-full px-2">
      <div className="flex-1 min-h-0 pb-36">
        {!selectedFile ? (
          NoSelectedFile()
        ) : (
          <div className="flex flex-col h-full min-h-0">
            <FileHeader file={selectedFile} />
            <div className="flex-1 min-h-0 overflow-y-auto">
              {isLoadingContent ? (
                <FileLoading />
              ) : contentError ? (
                <FileError message={contentError} />
              ) : onlyDiff && diffContentData ? (
                diffContentData.unchanged ? (
                  <UnChangedFile
                    diffContentData={diffContentData}
                    selectedFile={selectedFile}
                    comments={comments}
                    setComments={setComments}
                  />
                ) : (
                  <DiffViewer
                    diff={diffContentData.diff}
                    filePath={selectedFile}
                    comments={comments}
                    setComments={setComments}
                  />
                )
              ) : (
                <FileViewer
                  content={fileContent}
                  filePath={selectedFile}
                  comments={comments}
                  setComments={setComments}
                  isDiffView={false}
                />
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
