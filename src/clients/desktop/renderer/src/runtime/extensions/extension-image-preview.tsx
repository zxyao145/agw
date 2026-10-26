"use client";

import * as React from "react";
import { ImagePreview } from "@agw/chat";

import type { ExtensionHost } from "./extension-host";

/**
 * 显示视图请求的图片预览（`ui.previewImage`），与聊天中的图片预览相同；关闭时告知宿主。
 * Shows the image preview a view requested (`ui.previewImage`), the same as the chat's image preview, and tells the
 * host when it closes.
 */
export function ExtensionImagePreview({ host }: { host: ExtensionHost }) {
  const preview = React.useSyncExternalStore(
    host.subscribe,
    () => host.getSnapshot().imagePreview,
    () => null,
  );
  const image = React.useMemo(
    () => preview && { src: preview.src, alt: preview.name, name: preview.name },
    [preview],
  );
  if (!preview || !image) return null;
  return (
    <ImagePreview
      key={preview.id}
      image={image}
      onClose={() => host.closeImagePreview(preview.id)}
    />
  );
}
