"use client";

import * as React from "react";
import { FolderOutput, FolderInput } from "lucide-react";
import {
  listFiles,
  readFile,
  getFileDiff,
  type FileItem,
  type GitDiffResponse,
} from "@/api/files";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { DiffViewer } from "./diff-viewer";
import { FileExplorerProps } from "../../types";
import ColResizeSplit from "../split-layout";
import NoSelectedFile from "./no-selected-file";
import FileHeader from "./file-header";
import FileLoading from "./file-loading";
import FileError from "./file-error";
import FileViewer from "./file-viewer";
import UnChangedFile from "./unchanged-file";
import Export from "./explorer";

export function FileExplorer({
  rootDirectory,
  onFileSelect,
  comments,
  setComments,
}: FileExplorerProps) {
  const [selectedFile, setSelectedFile] = React.useState<string | null>(null);
  const [fileContent, setFileContent] = React.useState<string>("");
  const [isLoadingContent, setIsLoadingContent] = React.useState(false);
  const [contentError, setContentError] = React.useState<string | null>(null);
  const [showFileExplorer, setShowFileExplorer] = React.useState(true);
  const [onlyDiff, setOnlyDiff] = React.useState(true);
  const [recursiveMode, setRecursiveMode] = React.useState(true);
  const [diffContentData, setDiffContentData] =
    React.useState<GitDiffResponse | null>(null);

  const loadFileContent = React.useCallback(
    async (filePath: string) => {
      setIsLoadingContent(true);
      setContentError(null);
      setDiffContentData(null);

      try {
        if (onlyDiff) {
          const diff = await getFileDiff(filePath);
          setDiffContentData(diff);
          setFileContent("");
          setSelectedFile(filePath);
        } else {
          const content = await readFile(filePath);
          setFileContent(content);
          setDiffContentData(null);
          setSelectedFile(filePath);
        }
      } catch (err) {
        console.error("Error loading file:", err);
        setContentError((err as Error).message);
        setFileContent("");
        setDiffContentData(null);
      } finally {
        setIsLoadingContent(false);
      }
    },
    [onlyDiff],
  );


  const handleOnFileDeleted = React.useCallback((filePath: string) => {
    if (filePath === selectedFile) {
      setFileContent("");
      setDiffContentData(null);
    }
  }, []);

  const handleOnLoadFileContent = React.useCallback(
    (filePath: string) => {
      loadFileContent(filePath);
    },
    [loadFileContent],
  );

  const handleOnFileReseted = React.useCallback(
    (filePath: string | null) => {
      if (selectedFile && selectedFile === filePath) {
        loadFileContent(selectedFile);
      }
    },
    [loadFileContent],
  );

  const handleOnFileSelected = React.useCallback(
    (filePath: string | null) => {
      if (filePath && filePath !== selectedFile) {
        setSelectedFile(filePath);
        loadFileContent(filePath);

        if (onFileSelect) {
          onFileSelect(filePath);
        }
      }
    },
    [loadFileContent],
  );

  // Reload current file when diff mode changes
  React.useEffect(() => {
    if (selectedFile) {
      loadFileContent(selectedFile);
    }
  }, [onlyDiff]); // Only depend on diffMode, not loadFileContent to avoid infinite loop

  return (
    <div>
      {/* tools */}
      <div className="flex items-center gap-4">
        <Button
          variant="ghost"
          className="cursor-pointer"
          size="sm"
          onClick={() => setShowFileExplorer(!showFileExplorer)}
          title={showFileExplorer ? "Hide file explorer" : "Show file explorer"}
        >
          {showFileExplorer ? (
            <FolderOutput className="h-4 w-4" />
          ) : (
            <FolderInput className="h-4 w-4" />
          )}
        </Button>
        <div className="flex items-center gap-2">
          <Switch
            id="diff-mode"
            checked={onlyDiff}
            onCheckedChange={setOnlyDiff}
          />
          <Label htmlFor="diff-mode" className="text-sm cursor-pointer">
            Diff
          </Label>
        </div>
        {/* {onlyModified && (
          <div className="flex items-center gap-2">
            <Switch
              id="recursive-mode"
              checked={recursiveMode}
              onCheckedChange={setRecursiveMode}
            />
            <Label htmlFor="recursive-mode" className="text-sm cursor-pointer" title="Show all changed files recursively">
              Recursive
            </Label>
          </div>
        )} */}
      </div>
      <ColResizeSplit>
        <ColResizeSplit.Left>
          {!showFileExplorer ? null : (
            <Export
              rootDirectory={rootDirectory}
              onlyDiff={onlyDiff}
              recursiveMode={recursiveMode}
              onFileDeleted={handleOnFileDeleted}
              onFileSelected={handleOnFileSelected}
              onFileReseted={handleOnFileReseted}
              onLoadFileContent={handleOnLoadFileContent}
            />
          )}
        </ColResizeSplit.Left>
        <ColResizeSplit.Right>
          <div className="flex-1 flex flex-col min-h-full pb-36">
            {!selectedFile ? (
              NoSelectedFile()
            ) : (
              <div className="flex flex-col h-full">
                <FileHeader file={selectedFile} />
                <div className="flex-1">
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
        </ColResizeSplit.Right>
      </ColResizeSplit>
    </div>
  );
}
