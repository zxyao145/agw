import { defineView, type ExtensionApi } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";
import { useEffect, useState } from "react";
import { ProjectWorkspace } from "./project-workspace";
import "./styles.css";

function App({ api }: { api: ExtensionApi }) {
  const [context, setContext] = useState(api.context.get());
  useEffect(() => {
    const subscription = api.context.onDidChange(setContext);
    return () => subscription.dispose();
  }, [api]);
  return context.project ? (
    <ProjectWorkspace
      key={`${context.server.id}/${context.project.id}`}
      api={api}
      project={context.project}
    />
  ) : (
    <main>
      <h1>Bare Repository</h1>
      <p>Select a Project to manage its repositories and feature worktrees.</p>
    </main>
  );
}

const root = createRoot(document.getElementById("root")!);
defineView({
  onLoad(api) {
    api.theme.apply();
    root.render(<App api={api} />);
  },
  onUnload() {
    root.unmount();
  },
});
