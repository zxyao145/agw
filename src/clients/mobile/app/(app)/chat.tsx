import React from "react";

import { WorkspaceScreen } from "@/features/workspace/workspace-screen";

export default function ChatRoute(): React.JSX.Element {
  return <WorkspaceScreen initialTab="chat" />;
}
