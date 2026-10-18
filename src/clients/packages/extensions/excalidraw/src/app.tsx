import type { ExtensionApi, FileViewerFile, ThemeInfo } from "@agw/extension-sdk";
import { restore } from "@excalidraw/excalidraw";
import { useCallback, useEffect, useState, useSyncExternalStore } from "react";

import { Editor, type RestoredScene } from "./editor";
import { errorMessage } from "./error-message";
import { parseSceneFile } from "./scene-file";

type LoadState =
  | { status: "loading" }
  | { status: "ready"; scene: RestoredScene }
  | { status: "error"; message: string };

function readScene(text: string): RestoredScene {
  // 空文件（例如在 Files 中新建的文件）作为空白画布打开。An empty file (such as one just created in Files) opens as a blank canvas.
  const data = text.trim() === "" ? null : parseSceneFile(text);
  // 与 Excalidraw 自己打开文件时的选项相同。The options Excalidraw itself uses when opening a file.
  return restore(data, null, null, {
    repairBindings: true,
    refreshDimensions: false,
  });
}

function useThemeKind(api: ExtensionApi): ThemeInfo["kind"] {
  const subscribe = useCallback(
    (onChange: () => void) => {
      const subscription = api.theme.onDidChange(onChange);
      return () => subscription.dispose();
    },
    [api],
  );
  return useSyncExternalStore(subscribe, () => api.theme.get().kind);
}

export interface AppProps {
  api: ExtensionApi;
  file: FileViewerFile;
  reportUnsaved: (unsaved: boolean) => void;
}

/** 读取并显示一个文件；每个文件一个实例。Reads and shows one file; one instance per file. */
export function App({ api, file, reportUnsaved }: AppProps) {
  const theme = useThemeKind(api);
  const [state, setState] = useState<LoadState>({ status: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const scene = readScene(await api.fs.readTextFile(file.uri, { signal: controller.signal }));
        if (!controller.signal.aborted) setState({ status: "ready", scene });
      } catch (error) {
        // 中止说明视图已经卸载或换了文件。An abort means the view unloaded or moved to another file.
        if (!controller.signal.aborted) setState({ status: "error", message: errorMessage(error) });
      }
    })();
    return () => controller.abort();
  }, [api, file.uri]);

  if (state.status === "loading") {
    return <p className="status">Loading {file.name}…</p>;
  }
  if (state.status === "error") {
    return (
      <div className="status status-error" role="alert">
        <p className="status-title">Cannot open {file.name}</p>
        <p>{state.message}</p>
      </div>
    );
  }
  return (
    <Editor api={api} file={file} scene={state.scene} theme={theme} reportUnsaved={reportUnsaved} />
  );
}
