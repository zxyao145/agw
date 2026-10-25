"use client";

import * as React from "react";

const AppearanceRevisionContext = React.createContext<{ revision: unknown } | null>(null);

/**
 * 宿主外观的版本：值变化后，正在显示的查看器在下一帧重新读取颜色、字体与字号。宿主把它放在主题 provider 之内。
 * The host appearance revision: when it changes, the shown viewers read colors, font and size again on the next
 * frame. Hosts place it inside their theme provider.
 */
export function CodeViewAppearanceProvider({
  revision,
  children,
}: {
  revision: unknown;
  children: React.ReactNode;
}) {
  const value = React.useMemo(() => ({ revision }), [revision]);
  return (
    <AppearanceRevisionContext.Provider value={value}>
      {children}
    </AppearanceRevisionContext.Provider>
  );
}

/** 当前的外观版本；没有 provider 时抛出错误。The current appearance revision; throws without a provider. */
export function useCodeViewAppearanceRevision(): unknown {
  const value = React.useContext(AppearanceRevisionContext);
  if (value === null) throw new Error("CodeViewAppearanceProvider is missing.");
  return value.revision;
}
