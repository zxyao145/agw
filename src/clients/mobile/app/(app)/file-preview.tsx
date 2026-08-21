import { useLocalSearchParams } from "expo-router";
import React from "react";

import { FilePreviewScreen } from "@/features/files/file-preview-screen";

export default function FilePreviewRoute(): React.JSX.Element {
  const { path = "", diff = "false" } = useLocalSearchParams<{ path?: string; diff?: string }>();
  return <FilePreviewScreen path={path} diff={diff === "true"} />;
}
