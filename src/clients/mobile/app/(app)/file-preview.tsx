import { useLocalSearchParams } from "expo-router";
import React from "react";

import { FilePreviewScreen } from "@/features/files/file-preview-screen";

export default function FilePreviewRoute(): React.JSX.Element {
  const {
    projectId,
    directoryId,
    path = "",
    diff = "false",
  } = useLocalSearchParams<{
    projectId?: string;
    directoryId?: string;
    path?: string;
    diff?: string;
  }>();
  return (
    <FilePreviewScreen
      projectId={projectId}
      directoryId={directoryId}
      path={path}
      diff={diff === "true"}
    />
  );
}
