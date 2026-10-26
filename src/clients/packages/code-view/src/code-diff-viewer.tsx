"use client";

import * as React from "react";

import { useClientMounted } from "./client-mounted";
import type { CodeDiffViewerProps } from "./types";

const CodeDiffViewerImpl = React.lazy(() => import("./monaco/code-diff-viewer-impl"));

/**
 * 只读的左右两栏 diff 查看器。Monaco 在第一次显示时按需加载。
 * A read-only side-by-side diff viewer. Monaco loads on first display.
 */
export function CodeDiffViewer(props: CodeDiffViewerProps) {
  const mounted = useClientMounted();
  return (
    <div className="agw-code-view flex h-full min-h-0 flex-col">
      {mounted ? (
        <React.Suspense fallback={null}>
          <CodeDiffViewerImpl key={props.filePath} {...props} />
        </React.Suspense>
      ) : null}
    </div>
  );
}
