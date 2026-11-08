import type { ContextProject, ExtensionApi, RepositoryBlob } from "@agw/extension-sdk";
import { Eye, GitBranch } from "lucide-react";
import { useEffect, useState } from "react";
import { errorMessage, startView, type FileParams, type OpenRequest } from "./view";
import "./base.css";
import "./file-view.css";

function isFileParams(value: unknown): value is FileParams {
  const params = value as Partial<FileParams> | null;
  return Boolean(params?.repository && params.branch && params.path);
}

function formatSize(bytes: number): string {
  return bytes < 1024
    ? `${bytes} B`
    : bytes < 1024 * 1024
      ? `${(bytes / 1024).toFixed(1)} KB`
      : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** 未检出分支中文件的只读视图。A read-only view of a file in a branch that is not checked out. */
function RepositoryFile({
  api,
  project,
  request,
}: {
  api: ExtensionApi;
  project: ContextProject;
  request: OpenRequest;
}) {
  const [file, setFile] = useState<FileParams | null>(null);
  const [blob, setBlob] = useState<RepositoryBlob | null>(null);
  const [error, setError] = useState("");

  // 只有带文件参数的打开请求替换当前文件。Only open requests with file params replace the current file.
  useEffect(() => {
    if (isFileParams(request.params)) setFile(request.params);
  }, [request]);

  useEffect(() => {
    if (!file) return;
    let active = true;
    setBlob(null);
    setError("");
    api.repositories
      .blob({ projectId: project.id, ...file })
      .then((value) => active && setBlob(value))
      .catch((caught: unknown) => active && setError(errorMessage(caught)));
    return () => {
      active = false;
    };
  }, [api, project.id, file]);

  if (!file)
    return (
      <main className="centered">
        <p>Select a file in the Repositories sidebar.</p>
      </main>
    );

  const lines = blob?.content?.replace(/\r?\n$/u, "").split(/\r?\n/u) ?? [];
  return (
    <main className="file">
      <header className="file-head">
        <div className="file-title">
          <strong title={file.path}>{file.path.split("/").at(-1)}</strong>
          <span className="file-path">
            {file.repository}/{file.path}
          </span>
        </div>
        <div className="actions">
          <span className="badge">
            <GitBranch size={12} /> {file.branch}
          </span>
          <span className="badge muted">
            <Eye size={12} /> Read-only
          </span>
        </div>
      </header>
      {error ? (
        <p className="error inset" role="alert">
          {error}
        </p>
      ) : !blob ? (
        <p className="hint">Loading…</p>
      ) : blob.isBinary ? (
        <p className="hint">Binary file ({formatSize(blob.size)}); its contents are not shown.</p>
      ) : blob.tooLarge ? (
        <p className="hint">This file is too large to show ({formatSize(blob.size)}).</p>
      ) : (
        <div className="code" role="region" aria-label={`${file.path} at ${file.branch}`}>
          <table>
            <tbody>
              {lines.map((line, index) => (
                <tr key={index}>
                  <td className="line-number">{index + 1}</td>
                  <td className="line">{line || " "}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </main>
  );
}

startView(({ api, project, request }) => (
  <RepositoryFile api={api} project={project} request={request} />
));
