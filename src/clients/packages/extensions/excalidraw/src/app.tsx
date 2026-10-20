import type { ExtensionApi, FileViewerFile, ThemeInfo } from "@agw/extension-sdk";
import { useCallback, useSyncExternalStore } from "react";

import { Editor } from "./editor";

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
}

export function App({ api, file }: AppProps) {
  const theme = useThemeKind(api);
  return <Editor api={api} file={file} theme={theme} />;
}
