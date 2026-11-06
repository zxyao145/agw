import type { WorkspaceRepository } from "../../../packages/extension-sdk/src";
import { useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  CloneDialog,
  FeatureDialog,
} from "../../../packages/extensions/bare-repository/src/dialogs";
import "../../../packages/extensions/bare-repository/src/base.css";
import "../../../packages/extensions/bare-repository/src/styles.css";

const view = new URLSearchParams(location.search).get("view") ?? "clone";
const repositories: WorkspaceRepository[] = [
  {
    name: "project",
    path: view === "root-feature" ? "" : "project",
    defaultBranch: "main",
    branch: "main",
    dirty: false,
    ahead: 0,
    behind: 0,
    branches: ["main"],
    remoteBranches: ["origin/main"],
    error: null,
  },
];

function App() {
  const [event, setEvent] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [closed, setClosed] = useState(false);
  const operation = useRef<AbortSignal | null>(null);
  return (
    <>
      {!closed &&
        (view === "clone" ? (
          <CloneDialog
            busy={busy}
            error={undefined}
            onSubmit={(input, signal) => {
              operation.current = signal;
              setEvent(input);
              setBusy(true);
            }}
            onClose={() => {
              setEvent({ closed: true, aborted: operation.current?.aborted ?? false });
              setClosed(true);
            }}
          />
        ) : (
          <FeatureDialog
            repositories={repositories}
            busy={false}
            error={undefined}
            onSubmit={setEvent}
            onClose={() => setClosed(true)}
          />
        ))}
      <output aria-label="Submitted event">{JSON.stringify(event)}</output>
    </>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
