import React from "react";

import { WorkspaceScreen } from "@/features/workspace/workspace-screen";

export default function FilesRoute(): React.JSX.Element {
  return <WorkspaceScreen initialTab="files" />;
}
